using System.Windows.Threading;
using PrintDev.Core.Configuration;
using Serilog;

namespace PrintDev.Core.Hotkeys;

/// <summary>
/// Vigia os atalhos e tenta reavê-los quando outro programa os toma.
/// <para>
/// O caso que motivou isto: o PrtSc é a tecla mais disputada do Windows. Lightshot,
/// ShareX, Greenshot e até o OneDrive a registram, e quem chegar primeiro fica com ela.
/// Sem um vigia, o usuário fecha o concorrente e o Print Dev continua mudo, porque a
/// tentativa de registro aconteceu uma única vez, na inicialização.
/// </para>
/// <para>
/// O vigia roda na thread de interface (um <see cref="DispatcherTimer"/>), e não num
/// <see cref="System.Threading.Timer"/>: o <c>RegisterHotKey</c> só vale na thread dona da
/// janela, e de uma thread do pool ele falhava sempre com
/// <c>ERROR_WINDOW_OF_OTHER_THREAD</c> — um atalho tomado na partida nunca era recuperado.
/// Só o que demora e não toca no registro (varrer processos, encerrar o concorrente) vai
/// para uma thread do pool.
/// </para>
/// </summary>
public sealed class HotkeyGuardian : IDisposable
{
    /// <summary>
    /// De quanto em quanto tempo tentar reaver o atalho perdido. Trinta segundos é
    /// pouco o bastante para o usuário não perceber a espera e raro o bastante para não
    /// pesar: a verificação só varre a lista de processos quando há atalho falhando.
    /// </summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly HotkeyManager _manager;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private string _lastReportedProblem = string.Empty;
    private bool _running;
    private bool _again;
    private bool _disposed;

    public HotkeyGuardian(HotkeyManager manager, ISettingsService settings, ILogger log)
        : this(manager, settings, log, RetryInterval)
    {
    }

    /// <param name="retryInterval">Intervalo entre tentativas. Só os testes mudam o padrão.</param>
    internal HotkeyGuardian(HotkeyManager manager, ISettingsService settings, ILogger log, TimeSpan retryInterval)
    {
        _manager = manager;
        _settings = settings;
        _log = log.ForContext<HotkeyGuardian>();

        // A thread do gerente e a da janela dos atalhos: e nela que o vigia precisa rodar.
        _dispatcher = manager.Dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = retryInterval };
        _timer.Tick += OnTick;
    }

    /// <summary>
    /// Disparado quando há um conflito que depende de uma decisão do usuário.
    /// Enquanto não existe uma tela para perguntar, o conflito é apenas registrado.
    /// Sempre na thread de interface.
    /// </summary>
    public event EventHandler<HotkeyConflict>? ConflictDetected;

    /// <summary>
    /// Examina agora e passa a examinar periodicamente enquanto houver falha. Pode ser
    /// chamado de qualquer thread: o trabalho acontece na de interface.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_dispatcher.CheckAccess())
        {
            _ = InspectAsync();
        }
        else
        {
            _dispatcher.BeginInvoke(() => _ = InspectAsync());
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // Um disparo so: o proximo e armado, se for o caso, ao fim da verificacao.
        _timer.Stop();
        _ = InspectAsync();
    }

    /// <summary>
    /// Uma verificação por vez. Pedido que chega no meio de outra (a configuração mudou
    /// enquanto os processos eram varridos) faz a verificação repetir ao terminar, com o
    /// estado novo.
    /// </summary>
    private async Task InspectAsync()
    {
        if (_running)
        {
            _again = true;
            return;
        }

        _running = true;

        try
        {
            do
            {
                _again = false;
                await InspectOnceAsync();
            }
            while (_again && !_disposed);
        }
        catch (Exception exception)
        {
            // Nunca deixar escapar: a excecao de uma tarefa sem dono derruba o processo,
            // e o vigia e justamente o que existe para o programa nao ficar mudo.
            _log.Error(exception, "Falha inesperada no vigia dos atalhos");
            Reschedule(active: true);
        }
        finally
        {
            _running = false;
        }
    }

    private async Task InspectOnceAsync()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();

        IReadOnlyList<HotkeyRegistration> failures = _manager.RetryableFailures;
        if (failures.Count == 0)
        {
            _lastReportedProblem = string.Empty;
            Reschedule(active: false);
            return;
        }

        AdvancedSettings advanced = _settings.Current.Advanced;
        if (!advanced.GuardHotkey)
        {
            ReportOnce(Describe(failures, competitors: []));
            return;
        }

        // Varrer processos demora; fora da thread de interface para nao travar o painel.
        IReadOnlyList<Competitor> competitors = await Task.Run(CompetitorDetector.FindRunning);

        if (_disposed)
        {
            return;
        }

        if (competitors.Count > 0 && advanced.OnCompetitorDetected == CompetitorPolicy.AssumirAutomaticamente)
        {
            await TakeOverAsync(competitors);
            Reschedule(active: _manager.RetryableFailures.Count > 0);
            return;
        }

        // Sem encerrar ninguem, ainda vale tentar de novo: o usuario pode ter fechado o
        // concorrente na mao desde a ultima verificacao. O registro, esse sim, na thread
        // da janela -- que e esta, de volta do await.
        if (_manager.RetryFailures() > 0 && _manager.RetryableFailures.Count == 0)
        {
            _log.Information("Atalhos recuperados sem precisar encerrar nada.");
            _lastReportedProblem = string.Empty;
            Reschedule(active: false);
            return;
        }

        IReadOnlyList<HotkeyRegistration> stillFailing = _manager.RetryableFailures;
        ReportOnce(Describe(stillFailing, competitors));

        if (competitors.Count > 0 && advanced.OnCompetitorDetected == CompetitorPolicy.Perguntar)
        {
            ConflictDetected?.Invoke(this, new HotkeyConflict(stillFailing, competitors));
        }

        Reschedule(active: true);
    }

    private async Task TakeOverAsync(IReadOnlyList<Competitor> competitors)
    {
        string keys = string.Join(", ", _manager.RetryableFailures.Select(f => f.Text));

        foreach (Competitor competitor in competitors)
        {
            _log.Warning(
                "Encerrando {Programa} para assumir {Atalhos} (política: assumir automaticamente)",
                competitor.FriendlyName,
                keys);

            // Matar e esperar a saida pode levar segundos: fora da thread de interface.
            bool closed = await Task.Run(() => CompetitorDetector.Close(competitor));

            if (!closed)
            {
                _log.Warning("Não consegui encerrar {Programa} por completo.", competitor.FriendlyName);
            }
        }

        int recovered = _manager.RetryFailures();
        if (recovered > 0)
        {
            _log.Information("Assumi {Quantidade} atalho(s) depois de encerrar o concorrente.", recovered);
            _lastReportedProblem = string.Empty;
        }
    }

    /// <summary>
    /// Registra o problema uma vez só. Um vigia que repete a mesma linha a cada trinta
    /// segundos transforma o log em ruído e esconde o que importa.
    /// </summary>
    private void ReportOnce(string problem)
    {
        if (problem == _lastReportedProblem)
        {
            return;
        }

        _lastReportedProblem = problem;
        _log.Warning("{Problema}", problem);
    }

    private static string Describe(IReadOnlyList<HotkeyRegistration> failures, IReadOnlyList<Competitor> competitors)
    {
        string keys = string.Join(", ", failures.Select(f => f.Text));

        return competitors.Count == 0
            ? $"Atalho indisponível: {keys}. Nenhum capturador conhecido está rodando; pode ser outro programa."
            : $"Atalho indisponível: {keys}. Provavelmente é {string.Join(" ou ", competitors.Select(c => c.FriendlyName))}. "
              + "Feche esse programa, ou ponha \"aoDetectarConcorrente\": \"assumirAutomaticamente\" nas configurações.";
    }

    private void Reschedule(bool active)
    {
        if (_disposed)
        {
            return;
        }

        _timer.Stop();

        if (active)
        {
            _timer.Start();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}

/// <summary>Um conflito de atalho que depende de decisão do usuário.</summary>
/// <param name="Failures">Atalhos que não deu para registrar.</param>
/// <param name="Competitors">Programas suspeitos que estão rodando.</param>
public sealed record HotkeyConflict(
    IReadOnlyList<HotkeyRegistration> Failures,
    IReadOnlyList<Competitor> Competitors);
