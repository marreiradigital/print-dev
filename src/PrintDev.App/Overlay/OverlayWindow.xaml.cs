using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.Screens;

// O WPF tambem tem um System.Windows.Input.CaptureMode, que nao tem nada a ver com
// captura de tela. O apelido deixa explicito de quem estamos falando.
using CaptureMode = PrintDev.Core.Configuration.CaptureMode;

namespace PrintDev.Overlay;

/// <summary>
/// Uma janela do seletor. Existe uma por monitor.
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayCoordinator _coordinator;
    private SelectionLayer? _layer;
    private bool _closing;

    internal OverlayWindow(
        MonitorInfo monitor,
        FrozenScreen frozen,
        OverlayCoordinator coordinator)
    {
        Monitor = monitor;
        _coordinator = coordinator;

        InitializeComponent();

        // Cada janela mostra APENAS a fatia do seu monitor, e o recorte compartilha os
        // pixels do original - nao ha copia de imagem por monitor.
        FrozenView.Source = new CroppedBitmap(
            frozen.Bitmap,
            new Int32Rect(
                monitor.Bounds.Left - frozen.Bounds.Left,
                monitor.Bounds.Top - frozen.Bounds.Top,
                monitor.Bounds.Width,
                monitor.Bounds.Height));

        _layer = new SelectionLayer(monitor, frozen, coordinator.State);
        LayerHost.Content = _layer;

        SyncMode(coordinator.State.Mode);
        Hints.Visibility = coordinator.State.ShowHints ? Visibility.Visible : Visibility.Collapsed;

        if (coordinator.State.PickingColor)
        {
            // A barra de modos nao faz sentido no conta-gotas: nao ha o que selecionar.
            Toolbar.Visibility = Visibility.Collapsed;
        }

        ActionCopy.Click += (_, _) => coordinator.ConfirmWith(OverlayAction.Capturar);
        ActionAnnotate.Click += (_, _) => coordinator.ConfirmWith(OverlayAction.Anotar);
        ActionPin.Click += (_, _) => coordinator.ConfirmWith(OverlayAction.Fixar);
        ActionText.Click += (_, _) => coordinator.ConfirmWith(OverlayAction.Texto);
        ActionCloud.Click += (_, _) => coordinator.ConfirmWith(OverlayAction.Nuvem);

        ModeRegion.Checked += (_, _) => coordinator.SetMode(CaptureMode.Regiao);
        ModeWindow.Checked += (_, _) => coordinator.SetMode(CaptureMode.Janela);
        ModeMonitor.Checked += (_, _) => coordinator.SetMode(CaptureMode.Monitor);
        ModeEverything.Checked += (_, _) => coordinator.SetMode(CaptureMode.TelaInteira);
    }

    /// <summary>Monitor que esta janela cobre.</summary>
    internal MonitorInfo Monitor { get; }

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Posicionar FORA do WPF, em pixels fisicos. As propriedades Left, Top, Width e
        // Height do WPF sao unidades independentes de dispositivo e mudam de significado
        // conforme a escala do monitor - usa-las aqui deixaria a janela do monitor
        // secundario deslocada e do tamanho errado.
        IntPtr handle = new WindowInteropHelper(this).Handle;
        WindowPlacement.PlaceOnTop(handle, Monitor.Bounds);
    }

    /// <summary>Traz esta janela para o primeiro plano, para receber o teclado.</summary>
    internal void TakeFocus()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;

        // O direito de trazer janela para frente foi concedido ao processo quando o
        // atalho global foi acionado, entao esta chamada e aceita.
        WindowPlacement.BringToFront(handle);
        Activate();
        Focus();

        if (!_coordinator.State.PickingColor)
        {
            Toolbar.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Mostra, esconde e posiciona a barra de ações do modo de ajuste.
    /// <para>
    /// Fica embaixo da seleção, alinhada à direita, que é onde a mão termina o arrasto.
    /// Sem espaço embaixo, vai para cima; sem espaço em nenhum dos dois, para dentro da
    /// seleção. Nunca sai do monitor.
    /// </para>
    /// </summary>
    internal void SyncActionBar()
    {
        SelectionState state = _coordinator.State;
        bool show = state.ShowsActionBar && _coordinator.ActionBarMonitor() == Monitor.Handle;

        if (!show)
        {
            ActionBar.Visibility = Visibility.Collapsed;
            return;
        }

        PixelRect selection = state.Selection;
        ActionDimensions.Text = $"{selection.Width} × {selection.Height}";
        ActionBar.Visibility = Visibility.Visible;
        ActionBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double width = ActionBar.DesiredSize.Width;
        double height = ActionBar.DesiredSize.Height;
        const double gap = 8;

        (double left, double top) = Monitor.ToDeviceIndependent(selection.Left, selection.Top);
        (double right, double bottom) = Monitor.ToDeviceIndependent(selection.Right, selection.Bottom);

        double x = Math.Clamp(right - width, gap, Math.Max(gap, ActualWidth - width - gap));
        double y = bottom + gap;

        if (y + height > ActualHeight - gap)
        {
            y = top - gap - height;
        }

        if (y < gap)
        {
            y = Math.Max(top + gap, bottom - gap - height);
        }

        Canvas.SetLeft(ActionBar, x);
        Canvas.SetTop(ActionBar, y);
    }

    /// <summary>O ponteiro conforme a alça sob ele, no modo de ajuste.</summary>
    private void SyncCursor()
    {
        Cursor = _coordinator.GripUnderCursor() switch
        {
            SelectionGrip.Inside => Cursors.SizeAll,
            SelectionGrip.Left or SelectionGrip.Right => Cursors.SizeWE,
            SelectionGrip.Top or SelectionGrip.Bottom => Cursors.SizeNS,
            SelectionGrip.TopLeft or SelectionGrip.BottomRight => Cursors.SizeNWSE,
            SelectionGrip.TopRight or SelectionGrip.BottomLeft => Cursors.SizeNESW,
            _ => Cursors.Cross,
        };
    }

    /// <summary>Mostra a barra de modos sem pegar o foco. Serve ao aquecimento.</summary>
    internal void ShowToolbar() => Toolbar.Visibility = Visibility.Visible;

    /// <summary>Reflete o modo escolhido em outra janela, sem disparar o evento de volta.</summary>
    internal void SyncMode(CaptureMode mode)
    {
        RadioButton target = mode switch
        {
            CaptureMode.Janela => ModeWindow,
            CaptureMode.Monitor => ModeMonitor,
            CaptureMode.TelaInteira => ModeEverything,
            _ => ModeRegion,
        };

        if (!target.IsChecked.GetValueOrDefault())
        {
            target.IsChecked = true;
        }
    }

    /// <summary>Fecha sem passar pelo cancelamento — quem manda é o coordenador.</summary>
    internal void CloseOverlay()
    {
        _closing = true;
        Mouse.Capture(null);

        // Soltar a camada antes de fechar: ela segura a imagem congelada inteira, que em
        // dois monitores passa de dez megabytes. Tirar da arvore visual NAO basta: a
        // inscricao no estado compartilhado a manteria viva para sempre.
        _layer?.Detach();
        LayerHost.Content = null;
        _layer = null;
        FrozenView.Source = null;

        Close();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // A posicao vem do Windows, e nao do evento: durante um arrasto que saiu desta
        // janela, as coordenadas do evento sao relativas a ela e ficam negativas ou
        // maiores que o monitor. A verdade fisica esta no cursor do sistema.
        _coordinator.PointerMoved();
        SyncCursor();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (_coordinator.State.PickingColor)
        {
            _coordinator.ConfirmColor();
            return;
        }

        // Duplo clique dentro da selecao confirma, no modo de ajuste. Sem isto o segundo
        // clique comecaria um arrasto de mover.
        if (e.ClickCount == 2 && _coordinator.ConfirmByDoubleClick())
        {
            return;
        }

        Hints.Visibility = Visibility.Collapsed;
        _coordinator.DragStarted();

        // Capturar o mouse nesta janela faz os eventos continuarem chegando mesmo depois
        // de o cursor sair dela - e o que permite arrastar de um monitor para o outro.
        CaptureMouse();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        ReleaseMouseCapture();
        _coordinator.DragEnded();
    }

    /// <inheritdoc/>
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);

        // Botao direito cancela: e o reflexo de quem ja usou qualquer outro capturador.
        _coordinator.Cancel();
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _coordinator.ZoomMagnifier(e.Delta > 0 ? 2 : -2);
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        int step = shift ? 10 : 1;

        switch (e.Key)
        {
            case Key.Escape:
                _coordinator.EscapePressed();
                break;

            case Key.Enter:
                _coordinator.ConfirmCurrent();
                break;

            // As letras da barra de acoes, so com ela na tela.
            case Key.A when _coordinator.State.ShowsActionBar:
                _coordinator.ConfirmWith(OverlayAction.Anotar);
                break;

            case Key.F when _coordinator.State.ShowsActionBar:
                _coordinator.ConfirmWith(OverlayAction.Fixar);
                break;

            case Key.T when _coordinator.State.ShowsActionBar:
                _coordinator.ConfirmWith(OverlayAction.Texto);
                break;

            case Key.N when _coordinator.State.ShowsActionBar:
                _coordinator.ConfirmWith(OverlayAction.Nuvem);
                break;

            case Key.Tab:
                _coordinator.CycleMode();
                break;

            case Key.D1:
                _coordinator.SetMode(CaptureMode.Regiao);
                SyncMode(CaptureMode.Regiao);
                break;

            case Key.D2:
                _coordinator.SetMode(CaptureMode.Janela);
                SyncMode(CaptureMode.Janela);
                break;

            case Key.D3:
                _coordinator.SetMode(CaptureMode.Monitor);
                SyncMode(CaptureMode.Monitor);
                break;

            case Key.D4:
                _coordinator.SetMode(CaptureMode.TelaInteira);
                SyncMode(CaptureMode.TelaInteira);
                break;

            case Key.Left:
                Move(control, -step, 0);
                break;

            case Key.Right:
                Move(control, step, 0);
                break;

            case Key.Up:
                Move(control, 0, -step);
                break;

            case Key.Down:
                Move(control, 0, step);
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void Move(bool resizing, int dx, int dy)
    {
        if (resizing)
        {
            _coordinator.ResizeSelection(dx, dy);
        }
        else
        {
            _coordinator.NudgeSelection(dx, dy);
        }
    }

    /// <inheritdoc/>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        if (_closing)
        {
            return;
        }

        // Fechada por fora (Alt+F4, por exemplo): trata como cancelamento para o
        // coordenador nao ficar esperando uma resposta que nunca vem.
        e.Cancel = true;
        _coordinator.Cancel();
    }
}
