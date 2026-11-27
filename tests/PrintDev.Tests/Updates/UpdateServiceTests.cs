using System.IO;
using System.Net;
using System.Net.Http;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Updates;

namespace PrintDev.Tests.Updates;

/// <summary>
/// O serviço é testado inteiro, com o instalador trocado por um dublê que só conta quantas
/// vezes foi iniciado: nenhum teste executa instalador de verdade, nem toca no estado do
/// Print Dev que esteja instalado na máquina.
/// </summary>
public sealed class UpdateServiceTests : IDisposable
{
    private const string Tag = "v999.0.0";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "printdev-testes", Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _descartaveis = [];

    private sealed class Ambiente
    {
        public required UpdateService Service { get; init; }

        public required DubleDoInstalador Instalador { get; init; }

        public required UpdateStateStore Store { get; init; }

        public required ConfiguracoesFixas Settings { get; init; }

        public required Contador Consultas { get; init; }
    }

    private sealed class Contador
    {
        public int Valor;
    }

    private Ambiente Criar(
        UpdateAction acao,
        UpdateService.Timings? tempos = null,
        Func<int, Task<DownloadOutcome>>? aoBaixar = null)
    {
        var paths = new AppPaths(
            roamingRoot: Path.Combine(_root, "Roaming"),
            localRoot: Path.Combine(_root, "Local"),
            picturesRoot: Path.Combine(_root, "Imagens"));

        paths.EnsureCreated();

        var consultas = new Contador();
        string arquivo = Path.Combine(_root, "PrintDev-Setup-999.0.0.exe");
        File.WriteAllText(arquivo, "instalador de mentira");

        var instalador = new DubleDoInstalador(arquivo, aoBaixar);
        var settings = new ConfiguracoesFixas(new AppSettings { Updates = new UpdateSettings { Action = acao } });
        var store = new UpdateStateStore(paths, Serilog.Core.Logger.None);

        var github = new GitHubReleaseClient(
            new HttpClient(new LancamentoFixo(consultas)),
            Serilog.Core.Logger.None);

        var service = new UpdateService(
            github,
            instalador,
            store,
            settings,
            Serilog.Core.Logger.None,
            tempos ?? Rapido());

        _descartaveis.Add(service);

        return new Ambiente
        {
            Service = service,
            Instalador = instalador,
            Store = store,
            Settings = settings,
            Consultas = consultas,
        };
    }

    private static UpdateService.Timings Rapido() => new()
    {
        PrimeiraEspera = TimeSpan.FromMilliseconds(10),
        Intervalo = TimeSpan.FromHours(1),
        EsperaParaInstalar = TimeSpan.FromMilliseconds(20),
    };

    // ==================== Instalar assim que der ====================

    [Fact]
    public async Task Instalacao_automatica_inicia_o_instalador_e_pede_o_encerramento()
    {
        // O defeito: o resultado de InstalarAgora era ignorado e o programa nunca saia,
        // entao o instalador silencioso desistia (mutex de instancia unica preso) e a
        // tentativa ficava registrada como feita.
        Ambiente a = Criar(UpdateAction.InstalarAutomaticamente);
        var encerrou = new TaskCompletionSource();

        a.Service.PodeInterromper = () => true;
        a.Service.EncerrarParaInstalar = () => encerrou.TrySetResult();
        a.Service.Start();

        await encerrou.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, a.Instalador.Lancamentos);
    }

    [Fact]
    public async Task Com_janela_aberta_a_instalacao_automatica_pergunta_de_novo_ate_poder()
    {
        // Antes, "da para interromper?" era perguntado uma vez so, no segundo em que o
        // download terminava. Uma janela aberta ali bastava para nunca mais instalar.
        Ambiente a = Criar(UpdateAction.InstalarAutomaticamente);
        var encerrou = new TaskCompletionSource();
        int perguntas = 0;

        a.Service.PodeInterromper = () => Interlocked.Increment(ref perguntas) >= 4;
        a.Service.EncerrarParaInstalar = () => encerrou.TrySetResult();
        a.Service.Start();

        await encerrou.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(Volatile.Read(ref perguntas) >= 4);
        Assert.Equal(1, a.Instalador.Lancamentos);
    }

    [Fact]
    public async Task Instalador_que_nao_inicia_nao_marca_a_versao_como_tentada()
    {
        // A tentativa e gravada ANTES de iniciar. Se nada iniciou, deixar a marca faria
        // esta versao virar "a instalacao automatica falhou, instale manualmente" para
        // sempre, sem ninguem nunca ter tentado de verdade.
        Ambiente a = Criar(UpdateAction.InstalarAutomaticamente);
        a.Instalador.ResultadoDoLancamento = false;
        bool encerrou = false;

        a.Service.PodeInterromper = () => true;
        a.Service.EncerrarParaInstalar = () => encerrou = true;
        a.Service.Start();

        Assert.True(await EsperarAsync(() => a.Instalador.Lancamentos >= 1));
        Assert.True(await EsperarAsync(() => a.Service.InstaladorPronto is null));

        UpdateState estado = a.Store.Read();
        Assert.Null(estado.LastAttemptTag);
        Assert.Null(estado.ReadyInstaller);
        Assert.False(encerrou);
        Assert.Equal(UpdateStatus.Disponivel, a.Service.Last.Status);
    }

    // ==================== Menu da bandeja + encerramento ====================

    [Fact]
    public async Task Instalar_agora_e_depois_instalar_ao_sair_abre_um_instalador_so()
    {
        // O menu "Reiniciar e atualizar" chama InstalarAgora e depois encerra o programa,
        // e o encerramento chama InstalarAoSairSeHouver: o instalador abria duas vezes.
        Ambiente a = Criar(UpdateAction.InstalarAoSair);

        a.Service.Start();
        Assert.True(await EsperarAsync(() => a.Service.InstaladorPronto is not null));

        bool primeira = a.Service.InstalarAgora();
        bool aoSair = a.Service.InstalarAoSairSeHouver();

        Assert.True(primeira);
        Assert.False(aoSair);
        Assert.Equal(1, a.Instalador.Lancamentos);
    }

    // ==================== Tempo-limite do download ====================

    [Fact]
    public async Task Cancelamento_que_nao_e_o_do_encerramento_nao_para_as_verificacoes()
    {
        // O tempo-limite interno do download chegava ao laco como OperationCanceledException,
        // e o laco tratava qualquer uma como "o programa esta encerrando": um download
        // pendurado desligava todas as verificacoes futuras.
        Ambiente a = Criar(
            UpdateAction.InstalarAoSair,
            new UpdateService.Timings
            {
                PrimeiraEspera = TimeSpan.FromMilliseconds(10),
                Intervalo = TimeSpan.FromMilliseconds(60),
            },
            aoBaixar: numero => numero == 1
                ? throw new OperationCanceledException("tempo-limite do download")
                : Task.FromResult(new DownloadOutcome(false, null, "falhou de novo")));

        a.Service.Start();

        Assert.True(
            await EsperarAsync(() => a.Instalador.Downloads >= 2),
            "O laço de verificação parou depois do primeiro cancelamento.");
    }

    // ==================== Sessão encerrada pelo Windows ====================

    [Fact]
    public async Task Instalador_pendente_de_uma_sessao_anterior_e_aplicado_na_partida()
    {
        // "Instalar ao sair" nao roda quando o Windows termina a sessao sem passar pelo
        // encerramento do programa: o instalador conferido ficava parado em disco.
        Ambiente a = Criar(UpdateAction.InstalarAoSair);
        DeixarPendente(a);

        var encerrou = new TaskCompletionSource();
        a.Service.PodeInterromper = () => true;
        a.Service.EncerrarParaInstalar = () => encerrou.TrySetResult();
        a.Service.Start();

        await encerrou.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, a.Instalador.Lancamentos);
    }

    [Fact]
    public async Task Pendente_cuja_tentativa_ja_falhou_nao_e_repetido_na_partida()
    {
        Ambiente a = Criar(UpdateAction.InstalarAoSair);
        DeixarPendente(a, tentativaJaFeita: true);

        a.Service.PodeInterromper = () => true;
        a.Service.EncerrarParaInstalar = () => Assert.Fail("Não era para encerrar.");
        a.Service.Start();

        await Task.Delay(500);

        Assert.Equal(0, a.Instalador.Lancamentos);
    }

    [Fact]
    public async Task Pendente_nao_e_aplicado_na_partida_com_uma_janela_aberta()
    {
        Ambiente a = Criar(UpdateAction.InstalarAoSair);
        DeixarPendente(a);

        a.Service.PodeInterromper = () => false;
        a.Service.Start();

        await Task.Delay(500);

        // A janela vence: o instalador continua guardado para o encerramento normal.
        Assert.Equal(0, a.Instalador.Lancamentos);
        Assert.NotNull(a.Service.InstaladorPronto);
    }

    // ==================== Descarte ====================

    [Fact]
    public async Task Verificar_depois_de_descartar_nao_estoura()
    {
        Ambiente a = Criar(UpdateAction.InstalarAoSair);

        a.Service.Dispose();

        UpdateCheckResult resultado = await a.Service.CheckAsync(forcado: true);

        Assert.Equal(UpdateStatus.NaoVerificado, resultado.Status);
    }

    private void DeixarPendente(Ambiente a, bool tentativaJaFeita = false)
    {
        string arquivo = Path.Combine(_root, "pendente.exe");
        File.WriteAllText(arquivo, "instalador de mentira");

        a.Store.Write(new UpdateState
        {
            FoundTag = Tag,
            ReadyInstaller = arquivo,
            ReadyInstallerTag = Tag,
            LastCheck = DateTimeOffset.Now,
            LastAttemptTag = tentativaJaFeita ? Tag : null,
            LastAttemptAt = tentativaJaFeita ? DateTimeOffset.Now : null,
        });
    }

    private static async Task<bool> EsperarAsync(Func<bool> condicao)
    {
        DateTime limite = DateTime.UtcNow.AddSeconds(20);

        while (DateTime.UtcNow < limite)
        {
            if (condicao())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condicao();
    }

    public void Dispose()
    {
        foreach (IDisposable item in _descartaveis)
        {
            item.Dispose();
        }

        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // O laco do servico pode estar saindo de uma volta; a pasta e de teste.
            }
        }

        GC.SuppressFinalize(this);
    }

    // ==================== Dublês ====================

    private sealed class DubleDoInstalador : UpdateInstaller
    {
        private readonly string _arquivo;
        private readonly Func<int, Task<DownloadOutcome>>? _aoBaixar;
        private int _lancamentos;
        private int _downloads;

        public DubleDoInstalador(string arquivo, Func<int, Task<DownloadOutcome>>? aoBaixar)
            : base(new HttpClient(), Serilog.Core.Logger.None)
        {
            _arquivo = arquivo;
            _aoBaixar = aoBaixar;
        }

        public int Lancamentos => Volatile.Read(ref _lancamentos);

        public int Downloads => Volatile.Read(ref _downloads);

        public bool ResultadoDoLancamento { get; set; } = true;

        public override Task<DownloadOutcome> DownloadAsync(
            AvailableUpdate update,
            IProgress<double>? progress = null,
            CancellationToken cancellation = default)
        {
            int numero = Interlocked.Increment(ref _downloads);

            return _aoBaixar is not null
                ? _aoBaixar(numero)
                : Task.FromResult(new DownloadOutcome(true, _arquivo, "pronto"));
        }

        public override bool Launch(string installerPath, AppSettings settings)
        {
            Interlocked.Increment(ref _lancamentos);
            return ResultadoDoLancamento;
        }
    }

    private sealed class ConfiguracoesFixas(AppSettings atual) : ISettingsService
    {
        public AppSettings Current { get; } = atual;

#pragma warning disable CS0067 // o servico de atualizacao nunca assina
        public event EventHandler<AppSettings>? Changed;
#pragma warning restore CS0067

        public void Load()
        {
        }

        public void Update(Func<AppSettings, AppSettings> change)
        {
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Responde sempre com um lançamento 999.0.0 que tem instalador e digesto.</summary>
    private sealed class LancamentoFixo(Contador consultas) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref consultas.Valor);

            const string corpo = $$"""
                {
                  "tag_name": "{{Tag}}",
                  "draft": false,
                  "prerelease": false,
                  "body": "notas",
                  "assets": [
                    {
                      "name": "PrintDev-Setup-999.0.0.exe",
                      "size": 100,
                      "browser_download_url": "https://example.invalid/PrintDev-Setup-999.0.0.exe",
                      "digest": "sha256:aabbcc"
                    }
                  ]
                }
                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(corpo) });
        }
    }
}

/// <summary>
/// O teto de tempo do download é um cancelamento do próprio instalador, e não pode se
/// passar pelo pedido de parada de quem chamou.
/// </summary>
public sealed class UpdateInstallerTimeoutTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "printdev-testes", Guid.NewGuid().ToString("N"));

    private static AvailableUpdate Lancamento() => new(
        Version: new Version(999, 0, 0),
        Tag: "v999.0.0",
        Notes: string.Empty,
        DownloadUrl: "https://example.invalid/PrintDev-Setup-999.0.0.exe",
        AssetName: "PrintDev-Setup-999.0.0.exe",
        SizeBytes: 100,
        Sha256: "aabbcc");

    private UpdateInstaller Criar() => new(new HttpClient(new Pendurado()), Serilog.Core.Logger.None)
    {
        DownloadTimeout = TimeSpan.FromMilliseconds(150),
        DownloadFolder = _pasta,
    };

    [Fact]
    public async Task Estouro_do_teto_de_tempo_vira_falha_de_download_e_nao_excecao()
    {
        UpdateInstaller instalador = Criar();

        DownloadOutcome resultado = await instalador.DownloadAsync(Lancamento());

        Assert.False(resultado.Success);
        Assert.Contains("demorou demais", resultado.Message);
    }

    [Fact]
    public async Task Cancelamento_pedido_por_quem_chamou_continua_atravessando()
    {
        UpdateInstaller instalador = Criar();
        using var parada = new CancellationTokenSource();
        parada.CancelAfter(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => instalador.DownloadAsync(Lancamento(), progress: null, parada.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Aceita a conexão e nunca manda um byte.</summary>
    private sealed class Pendurado : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
