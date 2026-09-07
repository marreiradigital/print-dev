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
    private readonly Timer _timer;

    private string _lastReportedProblem = string.Empty;
    private bool _disposed;

    public HotkeyGuardian(HotkeyManager manager, ISettingsService settings, ILogger log)
    {
        _manager = manager;
        _settings = settings;
        _log = log.ForContext<HotkeyGuardian>();
        _timer = new Timer(_ => Inspect(), state: null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Disparado quando há um conflito que depende de uma decisão do usuário.
    /// Enquanto não existe uma tela para perguntar, o conflito é apenas registrado.
    /// </summary>
    public event EventHandler<HotkeyConflict>? ConflictDetected;

    /// <summary>Examina agora e passa a examinar periodicamente enquanto houver falha.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Inspect();
    }

    private void Inspect()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<HotkeyRegistration> failures = _manager.Failures;
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

        IReadOnlyList<Competitor> competitors = CompetitorDetector.FindRunning();

        if (competitors.Count > 0 && advanced.OnCompetitorDetected == CompetitorPolicy.AssumirAutomaticamente)
        {
            TakeOver(competitors, failures);
            Reschedule(active: _manager.Failures.Count > 0);
            return;
        }

        // Sem encerrar ninguem, ainda vale tentar de novo: o usuario pode ter fechado o
        // concorrente na mao desde a ultima verificacao.
        if (_manager.RetryFailures() > 0 && _manager.Failures.Count == 0)
        {
            _log.Information("Atalhos recuperados sem precisar encerrar nada.");
            _lastReportedProblem = string.Empty;
            Reschedule(active: false);
            return;
        }

        ReportOnce(Describe(_manager.Failures, competitors));

        if (competitors.Count > 0 && advanced.OnCompetitorDetected == CompetitorPolicy.Perguntar)
        {
            ConflictDetected?.Invoke(this, new HotkeyConflict(_manager.Failures, competitors));
        }

        Reschedule(active: true);
    }

    private void TakeOver(IReadOnlyList<Competitor> competitors, IReadOnlyList<HotkeyRegistration> failures)
    {
        foreach (Competitor competitor in competitors)
        {
            _log.Warning(
                "Encerrando {Programa} para assumir {Atalhos} (política: assumir automaticamente)",
                competitor.FriendlyName,
                string.Join(", ", failures.Select(f => f.Text)));

            if (!CompetitorDetector.Close(competitor))
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

        _timer.Change(active ? RetryInterval : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }
}

/// <summary>Um conflito de atalho que depende de decisão do usuário.</summary>
/// <param name="Failures">Atalhos que não deu para registrar.</param>
/// <param name="Competitors">Programas suspeitos que estão rodando.</param>
public sealed record HotkeyConflict(
    IReadOnlyList<HotkeyRegistration> Failures,
    IReadOnlyList<Competitor> Competitors);
