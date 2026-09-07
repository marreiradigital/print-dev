using PrintDev.Core.Capture;
using PrintDev.Core.Clipboard;
using PrintDev.Core.Configuration;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Paths;
using PrintDev.Core.Screens;
using Serilog;

namespace PrintDev.Capture;

/// <summary>
/// Recebe a ação de um atalho e a leva até uma captura gravada e publicada.
/// <para>
/// É o ponto onde as peças se encontram: descobrir o que capturar, congelar os pixels,
/// gravar o arquivo e publicar na área de transferência — nessa ordem, que não é
/// negociável.
/// </para>
/// </summary>
public sealed class CaptureCoordinator
{
    private readonly CapturePipeline _pipeline;
    private readonly ClipboardWriter _clipboard;
    private readonly HotkeyMessageWindow _messageWindow;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;

    private CaptureResult? _last;

    public CaptureCoordinator(
        CapturePipeline pipeline,
        ClipboardWriter clipboard,
        HotkeyMessageWindow messageWindow,
        ISettingsService settings,
        ILogger log)
    {
        _pipeline = pipeline;
        _clipboard = clipboard;
        _messageWindow = messageWindow;
        _settings = settings;
        _log = log.ForContext<CaptureCoordinator>();
    }

    /// <summary>Última captura desta sessão, para os atalhos de colagem forçada.</summary>
    public CaptureResult? Last => _last;

    /// <summary>Executa a ação pedida por um atalho.</summary>
    public CaptureResult? Execute(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.PasteAsPath:
                RepublishAndPaste(ClipboardContent.Caminho);
                return _last;

            case HotkeyAction.PasteAsImage:
                RepublishAndPaste(ClipboardContent.Imagem);
                return _last;
        }

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
            CaptureResult result = _pipeline.Save(image, mode, active);
            _last = result;

            if (_settings.Current.Clipboard.CopyAutomatically)
            {
                Publish(result, _settings.Current.Clipboard.Content);
            }

            return result;
        }
        catch (Exception exception)
        {
            // Falha de captura nunca pode derrubar o programa: o usuario aperta a tecla
            // de novo e a vida segue. O motivo fica no log.
            _log.Error(exception, "Falha ao capturar ({Modo})", mode);
            return null;
        }
    }

    /// <summary>
    /// Publica a captura na área de transferência no formato pedido.
    /// </summary>
    private bool Publish(CaptureResult result, ClipboardContent content)
    {
        ClipboardSettings settings = _settings.Current.Clipboard;

        bool wantsImage = content is ClipboardContent.Imagem or ClipboardContent.ImagemECaminho;
        bool wantsText = content is ClipboardContent.Caminho or ClipboardContent.ImagemECaminho;

        // Sem arquivo em disco nao ha caminho para colar - mas a imagem ainda vai.
        // Perder o arquivo e ruim; perder a captura seria pior.
        string? text = wantsText && result.SavedPath is not null
            ? PathTextFormatter.Format(result.SavedPath, settings)
            : null;

        var payload = new ClipboardPayload(
            Image: wantsImage ? result.Image : null,
            Text: text,
            FilePath: result.SavedPath,
            IncludeFileDrop: settings.IncludeFileDrop && result.SavedPath is not null);

        bool published = _clipboard.Write(_messageWindow.Handle, payload);

        if (published)
        {
            _log.Information(
                "Publicado na área de transferência: imagem={Imagem} texto={Texto} arquivo={Arquivo}",
                payload.Image is not null,
                payload.Text is not null,
                payload.IncludeFileDrop);
        }

        return published;
    }

    /// <summary>
    /// Republica a última captura num formato só e manda colar.
    /// <para>
    /// É o escape para quando o comportamento automático não é o desejado: colar a
    /// imagem num editor de texto que preferiria o caminho, ou o contrário.
    /// </para>
    /// </summary>
    private void RepublishAndPaste(ClipboardContent content)
    {
        if (_last is not { } last)
        {
            _log.Information("Nada para colar: nenhuma captura nesta sessão ainda.");
            return;
        }

        if (!Publish(last, content))
        {
            return;
        }

        PasteSender.SendPaste();
        _log.Debug("Colagem forçada como {Conteudo}", content);
    }

    private CaptureResult? NotYet(HotkeyAction action)
    {
        _log.Information("A ação {Acao} ainda não está implementada.", action);
        return null;
    }
}
