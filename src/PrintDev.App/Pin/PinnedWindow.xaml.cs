using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PrintDev.Core.Capture;
using PrintDev.Core.Screens;

namespace PrintDev.Pin;

/// <summary>
/// Uma captura fixada na tela, sempre por cima das outras janelas.
/// </summary>
public partial class PinnedWindow : Window
{
    private const double MinScale = 0.15;
    private const double MaxScale = 4.0;
    private const double MinOpacity = 0.25;

    private readonly CapturedImage _image;
    private readonly DispatcherTimer _hintTimer;
    private double _scale = 1;

    internal PinnedWindow(CapturedImage image)
    {
        _image = image;

        InitializeComponent();

        Picture.Source = image.ToBitmapSource();
        ApplyScale();

        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            Hint.Visibility = Visibility.Collapsed;
        };

        Loaded += (_, _) => _hintTimer.Start();
    }

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.RoundCorners(new WindowInteropHelper(this).Handle, small: true);
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // Aqui DragMove e o certo: a janela nao tem barra de titulo e nao precisa de
        // encaixe nem de menu de sistema - o arrasto e o unico gesto de mover que existe.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Close();
    }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Transparencia serve para comparar: com a captura semitransparente por cima
            // do que esta sendo feito, a diferenca entre os dois salta aos olhos.
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.08 : -0.08), MinOpacity, 1.0);
            return;
        }

        _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), MinScale, MaxScale);
        ApplyScale();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;

            case Key.D0 when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                _scale = 1;
                Opacity = 1;
                ApplyScale();
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private void ApplyScale()
    {
        Picture.Width = Math.Max(32, _image.Width * _scale);
        Picture.Height = Math.Max(32, _image.Height * _scale);
    }
}
