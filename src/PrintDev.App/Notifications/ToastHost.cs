using PrintDev.Core.Clipboard;
using PrintDev.Core.Configuration;
using PrintDev.Core.History;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Paths;
using PrintDev.Core.Runtime;
using PrintDev.Core.Screens;
using PrintDev.Core.Storage;
using Serilog;

namespace PrintDev.Notifications;

/// <summary>
/// Mostra os avisos de captura e cuida do empilhamento deles.
/// </summary>
public sealed class ToastHost
{
    /// <summary>
    /// Quantos avisos podem ficar visíveis ao mesmo tempo. Passar disso vira uma parede
    /// que cobre o canto da tela — e quem capturou dez vezes seguidas não está lendo
    /// aviso nenhum.
    /// </summary>
    private const int MaxVisible = 3;

    private const int EdgeMargin = 24;
    private const int StackGap = 8;
    private const int ToastWidth = 380;

    private readonly ISettingsService _settings;
    private readonly ClipboardWriter _clipboard;
    private readonly HotkeyMessageWindow _messageWindow;
    private readonly CaptureHistory _history;
    private readonly ILogger _log;
    private readonly List<ToastWindow> _visible = [];

    /// <summary>
    /// Pedido de anotação vindo do aviso.
    /// <para>
    /// É um evento, e não uma chamada direta ao coordenador de captura: o coordenador já
    /// depende deste host para mostrar o aviso, e a chamada de volta fecharia um ciclo
    /// que o container recusaria montar.
    /// </para>
    /// </summary>
    public event EventHandler<CaptureHistoryItem>? AnnotateRequested;

    public ToastHost(
        ISettingsService settings,
        ClipboardWriter clipboard,
        HotkeyMessageWindow messageWindow,
        CaptureHistory history,
        ILogger log)
    {
        _settings = settings;
        _clipboard = clipboard;
        _messageWindow = messageWindow;
        _history = history;
        _log = log.ForContext<ToastHost>();
    }

    /// <summary>
    /// Avisa que uma captura aconteceu.
    /// </summary>
    /// <param name="item">A captura.</param>
    /// <param name="near">
    /// Onde ela aconteceu. O aviso aparece no monitor que contém este retângulo, e não
    /// sempre na tela principal: quem capturou no monitor secundário está olhando para
    /// ele.
    /// </param>
    public void Show(CaptureHistoryItem item, PixelRect near)
    {
        ArgumentNullException.ThrowIfNull(item);

        CaptureSettings capture = _settings.Current.Capture;
        if (!capture.ShowToast)
        {
            return;
        }

        while (_visible.Count >= MaxVisible)
        {
            _visible[0].Dismiss();
            _visible.RemoveAt(0);
        }

        var toast = new ToastWindow(item, TimeSpan.FromSeconds(Math.Clamp(capture.ToastSeconds, 1, 30)));
        toast.ActionChosen += (sender, action) => Handle((ToastWindow)sender!, action);
        toast.Closed += (sender, _) =>
        {
            _visible.Remove((ToastWindow)sender!);
            Restack(near);
        };

        _visible.Add(toast);
        toast.ShowAt(SlotFor(near, _visible.Count - 1, toast.MeasuredHeight));
    }

    /// <summary>
    /// Calcula onde o aviso de índice indicado fica, empilhando de baixo para cima.
    /// </summary>
    private static PixelRect SlotFor(PixelRect near, int index, double height)
    {
        IReadOnlyList<MonitorInfo> monitors = VirtualDesktop.Enumerate();
        (int centerX, int centerY) = near.Center;
        MonitorInfo? monitor = VirtualDesktop.NearestMonitor(monitors, centerX, centerY)
                               ?? monitors.FirstOrDefault(m => m.IsPrimary);

        PixelRect area = monitor?.WorkArea ?? VirtualDesktop.BoundingBox();

        // A janela e medida em unidades independentes de dispositivo; o posicionamento e
        // em pixels fisicos. Sem a multiplicacao pela escala, o aviso sairia com o
        // tamanho errado num monitor com escala diferente de 100%.
        double scaleX = monitor?.ScaleX ?? 1;
        double scaleY = monitor?.ScaleY ?? 1;

        int physicalWidth = (int)Math.Round(ToastWidth * scaleX);
        int physicalHeight = (int)Math.Round(height * scaleY);
        int margin = (int)Math.Round(EdgeMargin * scaleX);
        int gap = (int)Math.Round(StackGap * scaleY);

        int left = area.Right - physicalWidth - margin;
        int top = area.Bottom - margin - ((physicalHeight + gap) * (index + 1));

        return new PixelRect(left, top, physicalWidth, physicalHeight);
    }

    private void Restack(PixelRect near)
    {
        for (int index = 0; index < _visible.Count; index++)
        {
            _visible[index].MoveTo(SlotFor(near, index, _visible[index].MeasuredHeight));
        }
    }

    private void Handle(ToastWindow toast, ToastAction action)
    {
        CaptureHistoryItem item = toast.Item;

        if (item.Path is null)
        {
            return;
        }

        switch (action)
        {
            case ToastAction.Annotate:
                AnnotateRequested?.Invoke(this, item);
                break;

            case ToastAction.CopyPath:
                CopyPath(item.Path);
                break;

            case ToastAction.Open:
                if (!ShellOpen.File(item.Path))
                {
                    _log.Warning("Não consegui abrir {Arquivo}", item.Path);
                }

                break;

            case ToastAction.Undo:
                Undo(item);
                break;
        }
    }

    private void CopyPath(string path)
    {
        string text = PathTextFormatter.Format(path, _settings.Current.Clipboard);
        _clipboard.Write(_messageWindow.Handle, new ClipboardPayload(Image: null, Text: text, FilePath: path));
        _log.Debug("Caminho copiado a pedido do aviso");
    }

    /// <summary>
    /// Desfaz a captura: manda o arquivo para a Lixeira e tira o histórico dele.
    /// <para>
    /// Vai para a Lixeira, e não para o nada, porque desfazer é reversível por
    /// definição — quem clicou errado precisa poder voltar atrás do voltar atrás.
    /// </para>
    /// </summary>
    private void Undo(CaptureHistoryItem item)
    {
        if (RecycleBin.Send(item.Path!))
        {
            _history.Remove(item);
            _log.Information("Captura desfeita: {Arquivo} foi para a Lixeira", item.Path);
        }
        else
        {
            _log.Warning("Não consegui mandar {Arquivo} para a Lixeira", item.Path);
        }
    }
}
