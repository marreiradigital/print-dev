using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PrintDev.Core.History;
using PrintDev.Core.Screens;

namespace PrintDev.Notifications;

/// <summary>
/// Um aviso de captura. Aparece no canto inferior direito do monitor onde a captura
/// aconteceu e some sozinho.
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _timer;
    private bool _dismissing;

    internal ToastWindow(CaptureHistoryItem item, TimeSpan lifetime)
    {
        InitializeComponent();

        Item = item;

        Thumbnail.Source = item.Thumbnail;
        FileName.Text = item.DisplayName;
        Dimensions.Text = $"{item.Width} × {item.Height}";

        // Sem arquivo em disco nao ha caminho para copiar nem o que abrir ou desfazer.
        bool saved = item.Path is not null;
        AnnotateButton.IsEnabled = saved;
        CopyPathButton.IsEnabled = saved;
        OpenButton.IsEnabled = saved;
        UndoButton.IsEnabled = saved;

        if (!saved)
        {
            Headline.Text = "Captura copiada (não deu para salvar)";
        }

        AnnotateButton.Click += (_, _) => Raise(ToastAction.Annotate);
        CopyPathButton.Click += (_, _) => Raise(ToastAction.CopyPath);
        OpenButton.Click += (_, _) => Raise(ToastAction.Open);
        UndoButton.Click += (_, _) => Raise(ToastAction.Undo);
        ThumbnailFrame.MouseLeftButtonUp += (_, _) => Raise(ToastAction.Open);

        _timer = new DispatcherTimer { Interval = lifetime };
        _timer.Tick += (_, _) => Dismiss();
    }

    /// <summary>A captura que este aviso representa.</summary>
    internal CaptureHistoryItem Item { get; }

    /// <summary>Disparado quando o usuário escolhe uma ação ou o aviso some.</summary>
    internal event EventHandler<ToastAction>? ActionChosen;

    /// <summary>Altura ocupada, para o empilhamento.</summary>
    internal double MeasuredHeight => ActualHeight > 0 ? ActualHeight : 132;

    /// <summary>Mostra o aviso ancorado neste retângulo, em pixels físicos.</summary>
    internal void ShowAt(PixelRect anchor)
    {
        Show();
        WindowPlacement.PlaceOnTop(new WindowInteropHelper(this).Handle, anchor);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));
        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220))
        {
            // Sai rapido e chega devagar: e a curva que da sensacao de leveza. Linear
            // pareceria mecanico, porque nada no mundo fisico se move assim.
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        };

        Root.BeginAnimation(OpacityProperty, fade);
        Slide.BeginAnimation(TranslateTransform.YProperty, slide);

        _timer.Start();
    }

    /// <summary>Reposiciona quando outro aviso some e a pilha se reorganiza.</summary>
    internal void MoveTo(PixelRect anchor)
        => WindowPlacement.PlaceOnTop(new WindowInteropHelper(this).Handle, anchor);

    /// <summary>Some com o aviso.</summary>
    internal void Dismiss()
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;
        _timer.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
    }

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        WindowPlacement.RoundCorners(handle);
    }

    /// <inheritdoc/>
    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);

        // Passar o mouse por cima segura o aviso: quem foi ler o nome do arquivo nao
        // pode ver o aviso fugir no meio da leitura.
        _timer.Stop();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);

        if (!_dismissing)
        {
            _timer.Start();
        }
    }

    /// <inheritdoc/>
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Dismiss();
    }

    private void Raise(ToastAction action)
    {
        ActionChosen?.Invoke(this, action);
        Dismiss();
    }

    /// <summary>Formata um número com separador de milhar, para o log e a interface.</summary>
    internal static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>O que o usuário escolheu no aviso.</summary>
internal enum ToastAction
{
    /// <summary>Abrir o editor de anotação com esta captura.</summary>
    Annotate,

    /// <summary>Copiar só o caminho, em texto.</summary>
    CopyPath,

    /// <summary>Abrir a imagem no programa associado.</summary>
    Open,

    /// <summary>Mandar o arquivo para a Lixeira e limpar a área de transferência.</summary>
    Undo,
}
