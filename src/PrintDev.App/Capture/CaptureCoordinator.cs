using PrintDev.Core.Capture;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Screens;
using Serilog;

namespace PrintDev.Capture;

/// <summary>
/// Recebe a ação de um atalho e a leva até uma captura gravada.
/// <para>
/// É o ponto onde as peças se encontram: descobrir o que capturar, congelar os pixels,
/// gravar o arquivo e — a partir da próxima fase — publicar na área de transferência.
/// </para>
/// </summary>
public sealed class CaptureCoordinator
{
    private readonly CapturePipeline _pipeline;
    private readonly ILogger _log;

    public CaptureCoordinator(CapturePipeline pipeline, ILogger log)
    {
        _pipeline = pipeline;
        _log = log.ForContext<CaptureCoordinator>();
    }

    /// <summary>Executa a ação pedida por um atalho.</summary>
    public CaptureResult? Execute(HotkeyAction action)
    {
        // A janela em primeiro plano precisa ser lida ANTES de qualquer coisa nossa
        // aparecer na tela, senao o nome que vai para o arquivo e o do proprio Print Dev.
        ActiveWindowInfo active = ActiveWindowInfo.Current();

        return action switch
        {
            HotkeyAction.Capture => CaptureCurrentMonitor(active, "regiao"),
            HotkeyAction.FullScreen => CaptureCurrentMonitor(active, "monitor"),
            HotkeyAction.ActiveWindow => CaptureActiveWindow(active),
            _ => NotYet(action),
        };
    }

    /// <summary>Captura todos os monitores num quadro só.</summary>
    public CaptureResult? CaptureEverything()
    {
        ActiveWindowInfo active = ActiveWindowInfo.Current();
        IReadOnlyList<MonitorInfo> monitors = VirtualDesktop.Enumerate();

        return Guarded(() => GdiScreenCapture.CaptureAllMonitors(monitors), "telaInteira", active);
    }

    private CaptureResult? CaptureCurrentMonitor(ActiveWindowInfo active, string mode)
    {
        IReadOnlyList<MonitorInfo> monitors = VirtualDesktop.Enumerate();
        MonitorInfo? monitor = VirtualDesktop.MonitorUnderCursor(monitors);

        if (monitor is null)
        {
            _log.Warning("Nenhum monitor encontrado sob o cursor.");
            return null;
        }

        return Guarded(() => GdiScreenCapture.CaptureRect(monitor.Bounds), mode, active);
    }

    private CaptureResult? CaptureActiveWindow(ActiveWindowInfo active)
    {
        if (active.Bounds.IsEmpty)
        {
            _log.Warning("Não consegui descobrir o retângulo da janela em primeiro plano.");
            return null;
        }

        // A janela pode estar parcialmente fora da tela: capturar so o que existe.
        PixelRect visible = active.Bounds.Intersect(VirtualDesktop.BoundingBox());
        if (visible.IsEmpty)
        {
            _log.Warning("A janela em primeiro plano está fora da área visível.");
            return null;
        }

        return Guarded(() => GdiScreenCapture.CaptureRect(visible), "janela", active);
    }

    private CaptureResult? Guarded(Func<CapturedImage> capture, string mode, ActiveWindowInfo active)
    {
        try
        {
            CapturedImage image = capture();
            return _pipeline.Save(image, mode, active);
        }
        catch (Exception exception)
        {
            // Falha de captura nunca pode derrubar o programa: o usuario aperta a tecla
            // de novo e a vida segue. O motivo fica no log.
            _log.Error(exception, "Falha ao capturar ({Modo})", mode);
            return null;
        }
    }

    private CaptureResult? NotYet(HotkeyAction action)
    {
        _log.Information("A ação {Acao} ainda não está implementada.", action);
        return null;
    }
}
