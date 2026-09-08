using PrintDev.Core.Configuration;
using Serilog;

namespace PrintDev.Core.Updates;

/// <summary>
/// Decide quando procurar, quando baixar e quando instalar.
/// <para>
/// A regra que governa tudo: um capturador de tela existe para estar pronto no
/// instante em que a tecla é apertada. Ele não pode sumir da bandeja por conta
/// própria. Por isso o padrão instala <b>ao sair</b>, e mesmo o modo automático
/// pergunta antes se dá para interromper.
/// </para>
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>
    /// Espera antes da primeira consulta. A inicialização deste programa leva cerca
    /// de meio segundo, e disputar largura de banda e disco com ela para perguntar
    /// algo que pode esperar seria trocar a partida rápida por nada.
    /// </summary>
    private static readonly TimeSpan PrimeiraEspera = TimeSpan.FromSeconds(60);

    private readonly GitHubReleaseClient _github;
    private readonly UpdateInstaller _installer;
    private readonly UpdateStateStore _store;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;

    private readonly CancellationTokenSource _parada = new();
    private readonly SemaphoreSlim _umDeCadaVez = new(1, 1);

    private Task? _laco;
    private bool _descartado;

    public UpdateService(
        GitHubReleaseClient github,
        UpdateInstaller installer,
        UpdateStateStore store,
        ISettingsService settings,
        ILogger log)
    {
        _github = github;
        _installer = installer;
        _store = store;
        _settings = settings;
        _log = log.ForContext<UpdateService>();
    }

    /// <summary>
    /// O último resultado conhecido. Dispara em thread de segundo plano — quem
    /// atualiza interface precisa passar pelo despachante.
    /// </summary>
    public event EventHandler<UpdateCheckResult>? Changed;

    /// <summary>Estado atual, para a interface desenhar sem provocar consulta.</summary>
    public UpdateCheckResult Last { get; private set; } =
        new(UpdateStatus.NaoVerificado, null, "Ainda não verificado.");

    /// <summary>
    /// Pergunta ao aplicativo se é hora de interromper. Devolve <see langword="false"/>
    /// com o seletor aberto, o editor de anotação em uso ou um pin na tela — momentos
    /// em que reiniciar destruiria trabalho não salvo.
    /// </summary>
    public Func<bool>? PodeInterromper { get; set; }

    /// <summary>Instalador baixado e conferido, esperando a hora.</summary>
    public string? InstaladorPronto { get; private set; }

    /// <summary>Liga a verificação periódica.</summary>
    public void Start()
    {
        if (_laco is not null || _descartado)
        {
            return;
        }

        AvisarSeATentativaAnteriorFalhou();

        _laco = Task.Run(() => LacoAsync(_parada.Token));
    }

    /// <summary>
    /// Procura agora. <paramref name="forcado"/> é o botão do painel: ignora o
    /// intervalo e o validador de cache, porque quem clicou quer uma resposta.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool forcado, CancellationToken cancellation = default)
    {
        if (!await _umDeCadaVez.WaitAsync(0, cancellation).ConfigureAwait(false))
        {
            return Last;
        }

        try
        {
            return await VerificarAsync(forcado, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _umDeCadaVez.Release();
        }
    }

    /// <summary>Nunca mais oferecer esta versão. A próxima volta a avisar.</summary>
    public void Ignorar(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        _store.Write(_store.Read() with { IgnoredTag = tag });
        _log.Information("Versão {Etiqueta} marcada como ignorada", tag);

        Publicar(UpdateCheckResult.Atualizado(ReleaseTag.Current));
    }

    /// <summary>
    /// Instala o que já está baixado e diz se o programa deve encerrar agora.
    /// <para>
    /// Devolver <see langword="true"/> obriga quem chamou a desligar em seguida: o
    /// instalador não substitui um executável em uso.
    /// </para>
    /// </summary>
    public bool InstalarAgora()
    {
        if (InstaladorPronto is null)
        {
            return false;
        }

        UpdateState estado = _store.Read();

        // Registrado ANTES de tentar. Depois de encerrar não há ninguém vivo para
        // anotar o fracasso, e é a partida seguinte que descobre o que houve.
        _store.Write(estado with
        {
            LastAttemptTag = estado.ReadyInstallerTag,
            LastAttemptAt = DateTimeOffset.Now,
        });

        return _installer.Launch(InstaladorPronto, _settings.Current);
    }

    /// <summary>
    /// Chamado no encerramento. Instala se a política for "ao sair" e houver algo
    /// pronto — sem nunca segurar o encerramento por causa de rede.
    /// </summary>
    public bool InstalarAoSairSeHouver()
    {
        if (_settings.Current.Updates.Action != UpdateAction.InstalarAoSair)
        {
            return false;
        }

        return InstaladorPronto is not null && InstalarAgora();
    }

    private async Task LacoAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(PrimeiraEspera, cancellation).ConfigureAwait(false);

            while (!cancellation.IsCancellationRequested)
            {
                if (await _umDeCadaVez.WaitAsync(0, cancellation).ConfigureAwait(false))
                {
                    try
                    {
                        await VerificarAsync(forcado: false, cancellation).ConfigureAwait(false);
                    }
                    catch (Exception excecao) when (excecao is not OperationCanceledException)
                    {
                        // Verificar atualização não pode derrubar o programa. Nunca.
                        _log.Error(excecao, "Falha inesperada na verificação de atualização");
                    }
                    finally
                    {
                        _umDeCadaVez.Release();
                    }
                }

                TimeSpan intervalo = IntervaloConfigurado();
                await Task.Delay(intervalo, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Encerramento normal.
        }
    }

    private async Task<UpdateCheckResult> VerificarAsync(bool forcado, CancellationToken cancellation)
    {
        UpdateSettings config = _settings.Current.Updates;

        if (config.Action == UpdateAction.Desligado && !forcado)
        {
            return Last;
        }

        UpdateState estado = _store.Read();

        if (!forcado && estado.LastCheck is { } ultima && DateTimeOffset.Now - ultima < IntervaloConfigurado())
        {
            // Já se perguntou há pouco. Reaproveita o que se sabe em vez de gastar
            // uma requisição para ouvir a mesma resposta.
            return Last;
        }

        ReleaseQuery consulta = await _github
            .FindLatestAsync(config.Repository, config.IncludePrereleases, forcado ? null : estado.ETag, cancellation)
            .ConfigureAwait(false);

        if (consulta.Error is not null)
        {
            _log.Debug("Verificação de atualização falhou: {Motivo}", consulta.Error);
            return Publicar(UpdateCheckResult.Falha(consulta.Error));
        }

        estado = estado with { LastCheck = DateTimeOffset.Now, ETag = consulta.ETag ?? estado.ETag };

        if (consulta.NotModified)
        {
            _store.Write(estado);
            return Last.Status == UpdateStatus.NaoVerificado
                ? Publicar(UpdateCheckResult.Atualizado(ReleaseTag.Current))
                : Last;
        }

        AvailableUpdate? achado = consulta.Release;

        if (achado is null || !ReleaseTag.Vale(achado.Version, ReleaseTag.Current))
        {
            _store.Write(estado with { FoundTag = achado?.Tag });
            return Publicar(UpdateCheckResult.Atualizado(ReleaseTag.Current));
        }

        estado = estado with { FoundTag = achado.Tag };

        if (string.Equals(estado.IgnoredTag, achado.Tag, StringComparison.OrdinalIgnoreCase))
        {
            _log.Debug("Versão {Etiqueta} disponível, mas o usuário pediu para pular", achado.Tag);
            _store.Write(estado);
            return Last;
        }

        // A tentativa anterior desta mesma versão já falhou. Insistir sozinho seria
        // repetir o mesmo fracasso a cada doze horas.
        if (string.Equals(estado.LastAttemptTag, achado.Tag, StringComparison.OrdinalIgnoreCase))
        {
            _store.Write(estado);
            return Publicar(new UpdateCheckResult(
                UpdateStatus.Disponivel,
                achado,
                $"A versão {achado.Version.ToString(3)} está disponível, mas a instalação automática falhou. Instale manualmente."));
        }

        _log.Information("Versão {Nova} disponível (estamos na {Atual})", achado.Version.ToString(3), ReleaseTag.Current.ToString(3));
        _store.Write(estado);

        if (config.Action == UpdateAction.SomenteAvisar || config.Action == UpdateAction.Desligado)
        {
            return Publicar(new UpdateCheckResult(
                UpdateStatus.Disponivel,
                achado,
                $"A versão {achado.Version.ToString(3)} está disponível."));
        }

        return await BaixarAsync(achado, estado, cancellation).ConfigureAwait(false);
    }

    private async Task<UpdateCheckResult> BaixarAsync(
        AvailableUpdate achado,
        UpdateState estado,
        CancellationToken cancellation)
    {
        DownloadOutcome download = await _installer
            .DownloadAsync(achado, progress: null, cancellation)
            .ConfigureAwait(false);

        if (!download.Success || download.Path is null)
        {
            return Publicar(new UpdateCheckResult(UpdateStatus.Disponivel, achado, download.Message));
        }

        InstaladorPronto = download.Path;
        _store.Write(estado with { ReadyInstaller = download.Path, ReadyInstallerTag = achado.Tag });

        UpdateCheckResult pronto = new(
            UpdateStatus.Baixado,
            achado,
            $"A versão {achado.Version.ToString(3)} está baixada e será instalada quando você sair.");

        if (_settings.Current.Updates.Action == UpdateAction.InstalarAutomaticamente
            && PodeInterromper?.Invoke() != false)
        {
            _log.Information("Instalando a {Versao} automaticamente", achado.Version.ToString(3));
            Publicar(pronto);
            InstalarAgora();
            return pronto;
        }

        return Publicar(pronto);
    }

    /// <summary>
    /// Na partida, confere se a instalação que o programa disparou antes de encerrar
    /// realmente aconteceu. Se a versão em execução ainda é a antiga, não aconteceu.
    /// </summary>
    private void AvisarSeATentativaAnteriorFalhou()
    {
        UpdateState estado = _store.Read();

        if (estado.LastAttemptTag is null || !ReleaseTag.TryParse(estado.LastAttemptTag, out Version? tentada))
        {
            return;
        }

        if (!ReleaseTag.Vale(tentada, ReleaseTag.Current))
        {
            // A versão tentada é a que está rodando: deu certo. Limpa o rastro para
            // não confundir a próxima atualização.
            _log.Information("Atualização para a {Versao} concluída", ReleaseTag.Current.ToString(3));
            _store.Write(estado with
            {
                LastAttemptTag = null,
                LastAttemptAt = null,
                ReadyInstaller = null,
                ReadyInstallerTag = null,
            });
            return;
        }

        _log.Warning(
            "A instalação da {Versao} foi disparada em {Quando} mas o programa continua na {Atual}",
            estado.LastAttemptTag,
            estado.LastAttemptAt,
            ReleaseTag.Current.ToString(3));
    }

    private TimeSpan IntervaloConfigurado()
    {
        // Um valor absurdo no arquivo de configuração não pode virar consulta em laço
        // nem verificação que nunca acontece.
        int horas = Math.Clamp(_settings.Current.Updates.IntervalHours, 1, 24 * 7);
        return TimeSpan.FromHours(horas);
    }

    private UpdateCheckResult Publicar(UpdateCheckResult resultado)
    {
        Last = resultado;
        Changed?.Invoke(this, resultado);
        return resultado;
    }

    public void Dispose()
    {
        if (_descartado)
        {
            return;
        }

        _descartado = true;

        _parada.Cancel();
        _parada.Dispose();
        _umDeCadaVez.Dispose();
    }
}
