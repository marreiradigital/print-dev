using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PrintDev.Core.Annotation;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.Screens;

namespace PrintDev.Annotation;

/// <summary>
/// Editor de anotação: setas, formas, texto, numeração de passos e a ferramenta de
/// esconder.
/// </summary>
public partial class AnnotationWindow : Window
{
    private readonly AnnotationCanvas _canvas;
    private Point _textAt;

    internal AnnotationWindow(CapturedImage image, AnnotationSettings settings)
    {
        InitializeComponent();

        _canvas = new AnnotationCanvas(image)
        {
            Color = ParseColor(settings.Color),
            Thickness = settings.Thickness,
            RedactionStyle = settings.RedactionStyle,
            RedactionStrength = settings.RedactionStrength,
        };

        if (TryFindResource("Font.Sans") is FontFamily family)
        {
            _canvas.UseTypeface(new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal));
        }

        CanvasHost.Children.Add(_canvas);

        _canvas.ItemsChanged += (_, _) => RefreshButtons();
        _canvas.TextRequested += (_, at) => BeginText(at);

        WireTools();
        WireColors();
        WireActions();
        SetupRedactStyles(settings.RedactionStyle);
        RefreshButtons();
    }

    /// <summary>A imagem final, com as marcas aplicadas. Nula se o usuário desistiu.</summary>
    internal CapturedImage? Result { get; private set; }

    /// <summary>Verdadeiro quando o usuário pediu para salvar, e não só copiar.</summary>
    internal bool ShouldSave { get; private set; }

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.RoundCorners(new WindowInteropHelper(this).Handle);
    }

    private void WireTools()
    {
        Bind(ToolArrow, AnnotationTool.Arrow);
        Bind(ToolRectangle, AnnotationTool.Rectangle);
        Bind(ToolEllipse, AnnotationTool.Ellipse);
        Bind(ToolPen, AnnotationTool.Pen);
        Bind(ToolHighlight, AnnotationTool.Highlight);
        Bind(ToolText, AnnotationTool.Text);
        Bind(ToolStep, AnnotationTool.Step);
        Bind(ToolRedact, AnnotationTool.Redact);

        void Bind(RadioButton button, AnnotationTool tool)
        {
            // Um grupo so: escolher uma ferramenta desmarca a anterior.
            button.GroupName = "ferramenta";
            button.Checked += (_, _) =>
            {
                _canvas.Tool = tool;

                // As opcoes de cor nao servem para a ferramenta de esconder, e as dela
                // nao servem para as outras. Mostrar as duas o tempo todo poluiria a
                // barra com controles que nao fazem nada.
                bool redacting = tool == AnnotationTool.Redact;
                ColorOptions.Visibility = redacting ? Visibility.Collapsed : Visibility.Visible;
                RedactOptions.Visibility = redacting ? Visibility.Visible : Visibility.Collapsed;
            };
        }
    }

    private void WireColors()
    {
        Bind(ColorRed, "#FFFF3B30");
        Bind(ColorAmber, "#FFFFC245");
        Bind(ColorGreen, "#FF3DDC97");
        Bind(ColorBlue, "#FF57B6FF");
        Bind(ColorMagenta, "#FFEC008C");
        Bind(ColorWhite, "#FFFFFFFF");

        ThinStroke.Checked += (_, _) => _canvas.Thickness = 2;
        MediumStroke.Checked += (_, _) => _canvas.Thickness = 4;
        ThickStroke.Checked += (_, _) => _canvas.Thickness = 7;

        void Bind(RadioButton button, string hex)
            => button.Checked += (_, _) => _canvas.Color = ParseColor(hex);
    }

    private void WireActions()
    {
        UndoButton.Click += (_, _) => _canvas.Undo();
        RedoButton.Click += (_, _) => _canvas.Redo();
        CloseButton.Click += (_, _) => Close();

        CopyButton.Click += (_, _) => Finish(save: false);
        SaveButton.Click += (_, _) => Finish(save: true);

        TextEntry.KeyDown += OnTextEntryKey;
        TextEntry.LostFocus += (_, _) => CommitText();
    }

    private void SetupRedactStyles(RedactionStyle current)
    {
        var options = new[]
        {
            (RedactionStyle.Pixelar, "Pixelar"),
            (RedactionStyle.Tarja, "Tarja sólida"),
            (RedactionStyle.Desfocar, "Desfocar"),
        };

        foreach ((RedactionStyle style, string label) in options)
        {
            RedactStyle.Items.Add(new ComboBoxItem { Content = label, Tag = style });
        }

        RedactStyle.SelectedIndex = Array.FindIndex(options, o => o.Item1 == current) is var index and >= 0
            ? index
            : 0;

        RedactStyle.SelectionChanged += (_, _) =>
        {
            if (RedactStyle.SelectedItem is ComboBoxItem { Tag: RedactionStyle style })
            {
                _canvas.RedactionStyle = style;
            }
        };
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (TextEntry.Visibility == Visibility.Visible)
        {
            return;
        }

        bool control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;

            case Key.Z when control:
                _canvas.Undo();
                break;

            case Key.Y when control:
                _canvas.Redo();
                break;

            case Key.Enter when control:
                Finish(save: false);
                break;

            case Key.D1: ToolArrow.IsChecked = true; break;
            case Key.D2: ToolRectangle.IsChecked = true; break;
            case Key.D3: ToolEllipse.IsChecked = true; break;
            case Key.D4: ToolPen.IsChecked = true; break;
            case Key.D5: ToolHighlight.IsChecked = true; break;
            case Key.D6: ToolText.IsChecked = true; break;
            case Key.D7: ToolStep.IsChecked = true; break;
            case Key.D8: ToolRedact.IsChecked = true; break;

            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Abre o campo de texto no ponto clicado.</summary>
    private void BeginText(Point at)
    {
        _textAt = at;

        // O campo aparece onde o texto vai ficar, e nao num canto: escrever longe do
        // lugar em que o texto entra obriga a imaginar o resultado.
        Point onScreen = _canvas.TranslatePoint(new Point(0, 0), CanvasHost);
        double scale = _canvas.ActualWidth <= 0 ? 1 : _canvas.ActualWidth / _canvas.RenderSize.Width;

        TextEntry.Text = string.Empty;
        TextEntry.Margin = new Thickness(
            CanvasHost.Margin.Left + onScreen.X + (at.X * scale),
            CanvasHost.Margin.Top + onScreen.Y + (at.Y * scale),
            0,
            0);

        TextEntry.Visibility = Visibility.Visible;
        TextEntry.Focus();
    }

    private void OnTextEntryKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TextEntry.Text = string.Empty;
            TextEntry.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void CommitText()
    {
        if (TextEntry.Visibility != Visibility.Visible)
        {
            return;
        }

        string text = TextEntry.Text;
        TextEntry.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _canvas.Add(new TextAnnotation(_textAt, text, Math.Max(14, _canvas.Thickness * 6))
        {
            Color = _canvas.Color,
            Thickness = _canvas.Thickness,
        });
    }

    private void Finish(bool save)
    {
        Result = _canvas.Export();
        ShouldSave = save;
        DialogResult = true;
    }

    private void RefreshButtons()
    {
        UndoButton.IsEnabled = _canvas.CanUndo;
        RedoButton.IsEnabled = _canvas.CanRedo;
    }

    private static Color ParseColor(string hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (Exception)
        {
            return Color.FromRgb(0xFF, 0x3B, 0x30);
        }
    }
}
