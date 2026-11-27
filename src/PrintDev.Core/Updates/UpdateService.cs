using System.IO;
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
    private readonly GitHubReleaseClient _github;
    private readonly UpdateInstaller _installer;
    private readonly UpdateStateStore _store;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;
    private readonly Timings _timings;

    private readonly CancellationTokenSource _parada = new();
    private readonly SemaphoreSlim _umDeCadaVez = new(1, 1);

    // Protege o instalador pronto e a marca de "ja estou esperando a hora de instalar".
    private readonly object _trava = new();

    private Task? _laco;
    private string? _instaladorPronto;
    private bool _aguardandoMomento;
    private volatile bool _descartado;

    public UpdateService(
        GitHubReleaseClient github,
        UpdateInstaller installer,
        UpdateStateStore store,
        ISettingsService settings,
        ILogger log)
        : this(github, installer, store, settings, log, Timings.Default)
    {
    }

    internal UpdateService(
        GitHubReleaseClient github,
        UpdateInstaller installer,
        UpdateStateStore store,
        ISettingsService settings,
        ILogger log,
        Timings timings)
    {
        _github = github;
        _installer = installer;
        _store = store;
        _settings = settings;
        _log = log.ForContext<UpdateService>();
        _timings = timings;
    }

    /// <summary>Os tempos do serviço. Os testes encurtam; o aplicativo usa os padrões.</summary>
    internal sealed record Timings
    {
        public static Timings Default { get; } = new();

        /// <summary>
        /// Espera antes da primeira consulta. A inicialização deste programa leva cerca
        /// de meio segundo, e disputar largura de banda e disco com ela para perguntar
        /// algo que pode esperar seria trocar a partida rápida por nada.
        /// </summary>
        public TimeSpan PrimeiraEspera { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>Intervalo entre consultas. Nulo usa o das configurações.</summary>
        public TimeSpan? Intervalo { get; init; }

        /// <summary>
        /// De quanto em quanto tempo perguntar de novo se já dá para interromper, quando
        /// a instalação automática encontrou uma janela aberta.
        /// </summary>
        public TimeSpan EsperaParaInstalar { get; init; } = TimeSpan.FromMinutes(1);
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

    /// <summary>
    /// Chamado quando o serviço mesmo iniciou o instalador (política automática, ou o
    /// instalador de uma sessão anterior que ficou pendente): o aplicativo precisa
    /// encerrar logo em seguida para liberar o executável. Dispara em thread de segundo
    /// plano — quem encerra precisa passar pelo despachante.
    /// <para>
    /// Sem isso o instalador silencioso encontrava o mutex de instância única ainda
    /// preso, desistia, e a tentativa ficava registrada como feita.
    /// </para>
    /// </summary>
    public Action? EncerrarParaInstalar { get; set; }

    /// <summary>Instalador baixado e conferido, esperando a hora.</summary>
    public string? InstaladorPronto
    {
        get
        {
            lock (_trava)
            {
                return _instaladorPronto;
            }
        }

        private set
        {
            lock (_trava)
            {
                _instaladorPronto = value;
            }
        }
    }

    /// <summary>Liga a verificação periódica.</summary>
    public void Start()
    {
        if (_laco is not null || _descartado)
        {
            return;
        }

        AvisarSeATentativaAnteriorFalhou();

        // O que ja se sabe vale desde o primeiro segundo. Esperar a primeira consulta
        // deixaria a bandeja e o painel dizendo "ainda nao verificado" por um minuto --
        // e, pior, esqueceria um instalador ja baixado que so faltava instalar.
        Publicar(DoEstado(_store.Read()));

        // O token e lido agora: dentro da lambda ele seria lido numa thread do pool, talvez
        // depois do Dispose.
        CancellationToken parada = _parada.Token;
        _laco = Task.Run(() => LacoAsync(parada));
    }

    /// <summary>
    /// Procura agora. <paramref name="forcado"/> é o botão do painel: ignora o
    /// intervalo e o validador de cache, porque quem clicou quer uma resposta.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(bool forcado, CancellationToken cancellation = default)
    {
        if (_descartado)
        {
            return Last;
        }

        try
        {
            if (!await _umDeCadaVez.WaitAsync(0, cancellation).ConfigureAwait(false))
            {
                return Last;
            }
        }
        catch (ObjectDisposedException)
        {
            return Last;
        }

        try
        {
            return await VerificarAsync(forcado, cancellation).ConfigureAwait(false);
        }
        finally
        {
            LiberarUm();
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
    /// <para>
    /// O instalador é consumido na primeira chamada que o inicia. O menu da bandeja
    /// chama aqui e depois encerra o programa, e o encerramento chama de novo (política
    /// "ao sair"): sem isso o instalador abria duas vezes.
    /// </para>
    /// </summary>
    public bool InstalarAgora()
    {
        string? instalador;

        lock (_trava)
        {
            instalador = _instaladorPronto;

            if (instalador is null)
            {
                return false;
            }

            _instaladorPronto = null;
        }

        UpdateState anterior = _store.Read();

        // Registrado ANTES de tentar. Depois de encerrar não há ninguém vivo para
        // anotar o fracasso, e é a partida seguinte que descobre o que houve.
        _store.Write(anterior with
        {
            LastAttemptTag = anterior.ReadyInstallerTag,
            LastAttemptAt = DateTimeOffset.Now,
        });

        bool iniciou;

        try
        {
            iniciou = _installer.Launch(instalador, _settings.Current);
        }
        catch (Exception excecao)
        {
            _log.Error(excecao, "Falha ao iniciar o instalador");
            iniciou = false;
        }

        if (iniciou)
        {
            return true;
        }

        // Nada foi iniciado, entao nao houve tentativa. Deixar a marca faria esta versao
        // ser tratada como "a instalacao automatica falhou" para sempre. O instalador que
        // nao abre (sumiu, bloqueado) e esquecido: a proxima consulta baixa outro.
        _store.Write(_store.Read() with
        {
            LastAttemptTag = anterior.LastAttemptTag,
            LastAttemptAt = anterior.LastAttemptAt,
            ReadyInstaller = null,
            ReadyInstallerTag = null,
        });

        Publicar(new UpdateCheckResult(
            UpdateStatus.Disponivel,
            Last.Update,
            "Não consegui iniciar o instalador. Procure atualizações de novo para baixá-lo outra vez."));

        return false;
    }

    /// <summary>
    /// Chamado no encerramento. Instala se a política for "ao sair" (ou automática, que
    /// ainda esperava uma folga) e houver algo pronto — sem nunca segurar o encerramento
    /// por causa de rede. Quem encerra o programa é quem chamou: aqui só se inicia o
    /// instalador.
    /// </summary>
    public bool InstalarAoSairSeHouver()
    {
        if (_settings.Current.Updates.Action is not (UpdateAction.InstalarAoSair or UpdateAction.InstalarAutomaticamente))
        {
            return false;
        }

        return InstaladorPronto is not null && InstalarAgora();
    }

    private async Task LacoAsync(CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(_timings.PrimeiraEspera, cancellation).ConfigureAwait(false);

            TentarInstalarPendenteDaSessaoAnterior();

            while (!cancellation.IsCancellationRequested)
            {
                if (await _umDeCadaVez.WaitAsync(0, cancellation).ConfigureAwait(false))
                {
                    try
                    {
                        await VerificarAsync(forcado: false, cancellation).ConfigureAwait(false);
                    }
                    catch (Exception excecao) when (!cancellation.IsCancellationRequested)
                    {
                        // Verificar atualização não pode derrubar o programa. Nunca. E
                        // nenhuma excecao encerra o laco: um cancelamento que NAO e o do
                        // encerramento (o tempo-limite de um download, por exemplo) e so
                        // uma verificacao que falhou -- tratar como "parar" deixava uma
                        // unica queda de conexao desligar todas as verificacoes futuras.
                        _log.Error(excecao, "Falha inesperada na verificação de atualização");
                    }
                    finally
                    {
                        LiberarUm();
                    }
                }

                TimeSpan intervalo = IntervaloConfigurado();
                await Task.Delay(intervalo, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Encerramento normal.
        }
        catch (ObjectDisposedException)
        {
            // O servico foi descartado no meio de uma volta.
        }
        catch (Exception excecao)
        {
            // Nada que escape daqui pode virar excecao de tarefa sem dono.
            _log.Error(excecao, "O laço de verificação de atualização terminou por erro");
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
            // uma requisição para ouvir a mesma resposta -- mas o que se sabe precisa
            // aparecer: devolver Last aqui deixaria o painel dizendo "ainda não
            // verificado" pelas doze horas seguintes a cada reinício, com a resposta
            // guardada em disco o tempo todo.
            return Last.Status == UpdateStatus.NaoVerificado ? Publicar(DoEstado(estado)) : Last;
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

        bool automatico = _settings.Current.Updates.Action == UpdateAction.InstalarAutomaticamente;

        UpdateCheckResult pronto = new(
            UpdateStatus.Baixado,
            achado,
            automatico
                ? $"A versão {achado.Version.ToString(3)} está baixada e será instalada assim que o programa estiver livre."
                : $"A versão {achado.Version.ToString(3)} está baixada e será instalada quando você sair.");

        Publicar(pronto);

        if (automatico)
        {
            AgendarInstalacaoAutomatica();
        }

        return pronto;
    }

    /// <summary>
    /// Instala sozinho assim que der, e reavalia enquanto não der.
    /// <para>
    /// "Dá para interromper" era perguntado uma única vez, no instante em que o
    /// download terminava. Se houvesse uma janela aberta naquele segundo, a resposta
    /// era não, ninguém perguntava de novo, e o instalador ficava parado para sempre —
    /// o modo automático não instalava nunca, e a política "ao sair" não se aplica a ele.
    /// </para>
    /// </summary>
    private void AgendarInstalacaoAutomatica()
    {
        CancellationToken parada;

        lock (_trava)
        {
            if (_aguardandoMomento || _descartado)
            {
                return;
            }

            try
            {
                parada = _parada.Token;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _aguardandoMomento = true;
        }

        _ = Task.Run(() => AguardarMomentoDeInstalarAsync(parada));
    }

    private async Task AguardarMomentoDeInstalarAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (!AindaDeveInstalarSozinho())
                {
                    return;
                }

                if (PodeInterromperAgora())
                {
                    _log.Information("Instalando a atualização automaticamente");
                    IniciarInstalacaoEEncerrar();
                    return;
                }

                await Task.Delay(_timings.EsperaParaInstalar, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Encerramento normal.
        }
        catch (Exception excecao)
        {
            _log.Error(excecao, "Falha ao esperar o momento de instalar a atualização");
        }
        finally
        {
            lock (_trava)
            {
                _aguardandoMomento = false;
            }
        }
    }

    /// <summary>
    /// Na partida, aplica o instalador que uma sessão anterior baixou e não chegou a
    /// instalar.
    /// <para>
    /// A política "ao sair" instala no encerramento do programa — mas quando o Windows
    /// desliga ou termina a sessão, o programa pode ser morto sem passar por ele, e o
    /// instalador já conferido ficava parado em disco para sempre. Este é o primeiro
    /// momento calmo seguinte. Só vale uma tentativa por instalador: se a anterior desta
    /// mesma versão já falhou, não se insiste.
    /// </para>
    /// </summary>
    private void TentarInstalarPendenteDaSessaoAnterior()
    {
        try
        {
            if (InstaladorPronto is null || TentativaJaFeita(_store.Read()))
            {
                return;
            }

            switch (_settings.Current.Updates.Action)
            {
                case UpdateAction.InstalarAutomaticamente:
                    AgendarInstalacaoAutomatica();
                    break;

                case UpdateAction.InstalarAoSair when PodeInterromperAgora():
                    _log.Information("Instalando a atualização que ficou pendente da sessão anterior");
                    IniciarInstalacaoEEncerrar();
                    break;
            }
        }
        catch (Exception excecao)
        {
            _log.Error(excecao, "Falha ao aplicar a atualização pendente");
        }
    }

    private bool AindaDeveInstalarSozinho()
        => InstaladorPronto is not null
           && _settings.Current.Updates.Action == UpdateAction.InstalarAutomaticamente
           && !TentativaJaFeita(_store.Read());

    private static bool TentativaJaFeita(UpdateState estado)
        => estado.LastAttemptTag is not null
           && string.Equals(estado.LastAttemptTag, estado.ReadyInstallerTag, StringComparison.OrdinalIgnoreCase);

    private void IniciarInstalacaoEEncerrar()
    {
        if (!InstalarAgora())
        {
            return;
        }

        if (EncerrarParaInstalar is null)
        {
            _log.Warning("O instalador foi iniciado, mas ninguém está ligado para encerrar o programa.");
            return;
        }

        try
        {
            EncerrarParaInstalar();
        }
        catch (Exception excecao)
        {
            _log.Error(excecao, "Falha ao pedir o encerramento para instalar a atualização");
        }
    }

    private bool PodeInterromperAgora()
    {
        try
        {
            return PodeInterromper?.Invoke() != false;
        }
        catch (Exception excecao)
        {
            // Aplicativo encerrando, despachante parado: nao e hora de interromper nada.
            _log.Debug(excecao, "Não consegui perguntar se dá para interromper");
            return false;
        }
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

    /// <summary>
    /// Reconstrói o que se sabe a partir do disco, sem tocar na rede.
    /// <para>
    /// É o que devolve o instalador já baixado depois de um reinício. Sem isto, uma
    /// atualização baixada ontem ficaria em disco para sempre: a política "instalar
    /// ao sair" não acharia nada em memória e nunca dispararia.
    /// </para>
    /// </summary>
    private UpdateCheckResult DoEstado(UpdateState estado)
    {
        if (!ReleaseTag.TryParse(estado.FoundTag, out Version? encontrada)
            || !ReleaseTag.Vale(encontrada, ReleaseTag.Current))
        {
            return UpdateCheckResult.Atualizado(ReleaseTag.Current);
        }

        if (string.Equals(estado.IgnoredTag, estado.FoundTag, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCheckResult.Atualizado(ReleaseTag.Current);
        }

        // O instalador só conta se ainda estiver em disco: a pasta é temporária, e o
        // Windows a limpa sozinho quando quer.
        bool prontoEmDisco = estado.ReadyInstaller is not null
                             && string.Equals(estado.ReadyInstallerTag, estado.FoundTag, StringComparison.OrdinalIgnoreCase)
                             && File.Exists(estado.ReadyInstaller);

        if (prontoEmDisco)
        {
            InstaladorPronto = estado.ReadyInstaller;

            return new UpdateCheckResult(
                UpdateStatus.Baixado,
                null,
                $"A versão {encontrada.ToString(3)} está baixada e será instalada quando você sair.");
        }

        return new UpdateCheckResult(
            UpdateStatus.Disponivel,
            null,
            $"A versão {encontrada.ToString(3)} está disponível.");
    }

    private TimeSpan IntervaloConfigurado()
    {
        if (_timings.Intervalo is { } fixo)
        {
            return fixo;
        }

        // Um valor absurdo no arquivo de configuração não pode virar consulta em laço
        // nem verificação que nunca acontece.
        int horas = Math.Clamp(_settings.Current.Updates.IntervalHours, 1, 24 * 7);
        return TimeSpan.FromHours(horas);
    }

    private UpdateCheckResult Publicar(UpdateCheckResult resultado)
    {
        // Em nível de informação, e não de depuração: isto acontece duas vezes por dia,
        // então não é ruído — e sem ele o subsistema fica mudo. "Não me avisou da versão
        // nova" viraria uma investigação sem uma única linha por onde começar.
        _log.Information(
            "Verificação de atualização: {Situacao} — {Mensagem}",
            resultado.Status,
            resultado.Message);

        Last = resultado;
        Changed?.Invoke(this, resultado);
        return resultado;
    }

    /// <summary>
    /// Solta o semáforo sem estourar: o laço de verificação pode estar no fim de uma
    /// volta quando o serviço é descartado, e o <c>Release</c> do <c>finally</c> dele
    /// encontrava o semáforo já descartado.
    /// </summary>
    private void LiberarUm()
    {
        try
        {
            _umDeCadaVez.Release();
        }
        catch (ObjectDisposedException)
        {
            // Descartado enquanto a verificacao terminava.
        }
    }

    public void Dispose()
    {
        if (_descartado)
        {
            return;
        }

        _descartado = true;

        try
        {
            _parada.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // O laco e a espera de instalar ainda podem estar saindo de uma volta; nao se
        // espera por eles (quem descarta e a thread de interface, e uma delas pergunta
        // "dá para interromper" a ela). O que importa e que o fim delas nao estoure.
        _parada.Dispose();
        _umDeCadaVez.Dispose();
    }
}
