using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    /// <summary>
    /// Largura do aviso, em unidades independentes de dispositivo.
    /// <para>
    /// Mora aqui e não no XAML porque o empilhamento precisa do mesmo número para
    /// calcular a posição em pixels físicos. Dois lugares com o valor escrito à mão é
    /// como um deles fica para trás na primeira vez que a largura muda.
    /// </para>
    /// </summary>
    internal const double WidthDip = 470;

    private readonly DispatcherTimer _timer;
    private bool _dismissing;

    internal ToastWindow(CaptureHistoryItem item, TimeSpan lifetime)
    {
        InitializeComponent();

        Width = WidthDip;
        Item = item;

        Thumbnail.Source = item.Thumbnail;
        FileName.Text = item.DisplayName;
        Dimensions.Text = $"{item.Width} × {item.Height}";

        // Sem arquivo em disco nao ha caminho para copiar nem o que abrir ou desfazer.
        bool saved = item.Path is not null;
        AnnotateButton.IsEnabled = saved;
        PinButton.IsEnabled = saved;
        CopyPathButton.IsEnabled = saved;

        // Enviar exige arquivo em disco: e o arquivo que sobe.
        CloudButton.IsEnabled = saved;

        // Desfazer continua valendo sem arquivo: sobra tirar do histórico e devolver a
        // área de transferência. Desabilitar aqui deixaria a captura indesejada colada.
        UndoButton.IsEnabled = true;

        if (!saved)
        {
            Headline.Text = "Captura copiada (não deu para salvar)";
        }

        AnnotateButton.Click += (_, _) => Raise(ToastAction.Annotate);
        PinButton.Click += (_, _) => Raise(ToastAction.Pin);
        CopyPathButton.Click += (_, _) => Raise(ToastAction.CopyPath);
        UndoButton.Click += (_, _) => Raise(ToastAction.Undo);
        CloudButton.Click += (_, _) => Raise(ToastAction.Cloud);
        // Abrir sai pela miniatura, e nao por um botao proprio: o aviso ja carrega os
        // botoes que cabem, e o clique na imagem e o gesto que a pessoa ja tenta.
        ThumbnailFrame.MouseLeftButtonUp += (_, _) => Raise(ToastAction.Open);

        _timer = new DispatcherTimer { Interval = lifetime };
        _timer.Tick += (_, _) => Dismiss();
    }

    /// <summary>A captura que este aviso representa.</summary>
    internal CaptureHistoryItem Item { get; }

    /// <summary>Se o aviso do primeiro envio já foi mostrado neste aviso.</summary>
    internal bool CloudConfirmationAsked { get; private set; }

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

    /// <summary>
    /// Troca o aviso por uma mensagem de falha, em vez de sumir.
    /// <para>
    /// Ação que falha em silêncio é pior que ação que não existe: a pessoa fica achando
    /// que deu certo. O aviso para de contar o tempo e só sai por clique.
    /// </para>
    /// </summary>
    internal void ShowFailure(string message)
    {
        _timer.Stop();

        Headline.Text = message;
        Headline.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");

        AnnotateButton.IsEnabled = false;
        PinButton.IsEnabled = false;
        CopyPathButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
        CloudButton.IsEnabled = false;
    }

    /// <summary>
    /// Segura o aviso na tela enquanto o envio acontece.
    /// <para>
    /// Sem isto o aviso sumiria no meio do envio e o link chegaria sem ninguém para
    /// ver — a pessoa clicou e ficou sem resposta.
    /// </para>
    /// </summary>
    internal void ShowSending()
    {
        _timer.Stop();

        Headline.Text = "Enviando para a nuvem…";
        CloudButton.IsEnabled = false;
        UndoButton.IsEnabled = false;
    }

    /// <summary>
    /// Pergunta a confirmação do primeiro envio dentro do próprio aviso.
    /// <para>
    /// Uma caixa de diálogo aqui roubaria o foco de quem está digitando — que é
    /// exatamente o que este aviso existe para não fazer.
    /// </para>
    /// </summary>
    internal void AskCloudConfirmation()
    {
        _timer.Stop();
        CloudConfirmationAsked = true;

        Headline.Text = "A imagem vai sair da sua máquina.";
        FileName.Text = "Link público de 48 horas, para quem tiver o endereço.";
        CloudButton.Content = "Confirmar envio";
    }

    /// <summary>Mostra o desfecho do envio e reinicia a contagem, com folga para ler.</summary>
    internal void ShowCloudResult(string message, bool success, TimeSpan lifetime)
    {
        Headline.Text = message;

        if (!success)
        {
            Headline.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        }

        CloudButton.Content = "Nuvem";
        CloudButton.IsEnabled = !success;
        UndoButton.IsEnabled = true;

        if (_dismissing)
        {
            return;
        }

        _timer.Interval = lifetime;
        _timer.Start();
    }

    /// <summary>
    /// Quem trata a ação decide se o aviso some. É o que permite manter o aviso na tela
    /// com a mensagem de erro quando desfazer não dá certo.
    /// </summary>
    private void Raise(ToastAction action) => ActionChosen?.Invoke(this, action);

    /// <summary>Formata um número com separador de milhar, para o log e a interface.</summary>
    internal static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>O que o usuário escolheu no aviso.</summary>
internal enum ToastAction
{
    /// <summary>Abrir o editor de anotação com esta captura.</summary>
    Annotate,

    /// <summary>Fixar a captura na tela, sempre por cima.</summary>
    Pin,

    /// <summary>Copiar só o caminho, em texto.</summary>
    CopyPath,

    /// <summary>Abrir a imagem no programa associado.</summary>
    Open,

    /// <summary>Mandar o arquivo para a Lixeira e limpar a área de transferência.</summary>
    Undo,

    /// <summary>Publicar no serviço temporário e copiar o link.</summary>
    Cloud,
}
