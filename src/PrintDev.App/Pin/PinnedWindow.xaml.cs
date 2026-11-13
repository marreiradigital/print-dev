using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PrintDev.Core.Capture;
using PrintDev.Core.Runtime;
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

    /// <summary>Espessura da moldura, em unidades do WPF; igual à do XAML.</summary>
    private const double FrameThickness = 2;

    /// <summary>A dica só aparece se couber na imagem sem alargar a janela.</summary>
    private const double HintMinWidth = 380;
    private const double HintMinHeight = 90;

    private readonly int _imageWidth;
    private readonly int _imageHeight;
    private readonly PixelRect? _origin;
    private readonly DispatcherTimer _hintTimer;
    private double _scale = 1;
    private double _dpi = 1;

    /// <param name="image">A imagem a fixar.</param>
    /// <param name="origin">
    /// Onde ela estava na tela, em pixels físicos. Com ele o pin abre exatamente em cima
    /// da área recortada — é o "fixar no lugar" do seletor. Sem ele, o Windows escolhe.
    /// </param>
    internal PinnedWindow(CapturedImage image, PixelRect? origin = null)
    {
        _origin = origin;

        // So as dimensoes ficam: os bytes crus ja viraram o bitmap da tela, e guarda-los
        // tambem dobraria o custo de cada pin enquanto ele estiver aberto.
        _imageWidth = image.Width;
        _imageHeight = image.Height;

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

        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ApplyScale();
        MoveToOrigin();
    }

    /// <summary>
    /// Mudou de monitor, e a escala junto: a imagem continua um pixel por pixel de tela.
    /// </summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi = newDpi.DpiScaleX;
        ApplyScale();
    }

    /// <summary>
    /// Põe a imagem exatamente sobre a área de onde ela saiu: a janela começa a moldura
    /// antes, para o primeiro pixel da imagem cair no primeiro pixel da área.
    /// </summary>
    private void MoveToOrigin()
    {
        if (_origin is not { } origin)
        {
            return;
        }

        int frame = (int)Math.Round(FrameThickness * _dpi);
        WindowPlacement.MoveTo(new WindowInteropHelper(this).Handle, origin.Left - frame, origin.Top - frame);
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

    /// <summary>
    /// Tamanho da imagem na tela. Dividido pela escala do monitor: em 150%, uma unidade
    /// do WPF vale 1,5 pixel, e sem a divisão o pin saía uma vez e meia maior que a área
    /// de origem — e borrado, porque cada pixel virava um pixel e meio.
    /// </summary>
    private void ApplyScale()
    {
        Picture.Width = Math.Max(32 / _dpi, _imageWidth * _scale / _dpi);
        Picture.Height = Math.Max(32 / _dpi, _imageHeight * _scale / _dpi);

        // Recorte pequeno: a dica alargaria a janela para além da imagem.
        if (Picture.Width < HintMinWidth || Picture.Height < HintMinHeight)
        {
            Hint.Visibility = Visibility.Collapsed;
        }
    }

    /// <inheritdoc/>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Picture.Source = null;
        WorkingSet.ReleaseSoon();
    }
}
