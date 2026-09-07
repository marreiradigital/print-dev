using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintDev.Core.Annotation;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;

namespace PrintDev.Annotation;

/// <summary>Ferramenta em uso no editor.</summary>
public enum AnnotationTool
{
    Arrow,
    Rectangle,
    Ellipse,
    Pen,
    Highlight,
    Text,
    Step,
    Redact,
}

/// <summary>
/// A área de desenho do editor: mostra a captura e recebe as marcas.
/// <para>
/// Desenha tudo numa passada de <see cref="DrawingContext"/>, em vez de manter um objeto
/// visual por marca. Cada movimento do mouse redesenha, e uma imagem de tela cheia com
/// vinte marcas não pode custar vinte atualizações de árvore visual por quadro.
/// </para>
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private readonly List<AnnotationItem> _items = [];
    private readonly List<AnnotationItem> _undone = [];

    private CapturedImage _source;
    private BitmapSource _bitmap;
    private Typeface _typeface = new("Segoe UI");

    private Point _dragStart;
    private Point _dragCurrent;
    private bool _dragging;
    private List<Point>? _freehand;

    public AnnotationCanvas(CapturedImage source)
    {
        _source = source;
        _bitmap = source.ToBitmapSource();

        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Cross;
    }

    /// <summary>Ferramenta escolhida na barra.</summary>
    public AnnotationTool Tool { get; set; } = AnnotationTool.Arrow;

    /// <summary>Cor do traço.</summary>
    public Color Color { get; set; } = Color.FromRgb(0xFF, 0x3B, 0x30);

    /// <summary>Espessura do traço, em pixels da imagem.</summary>
    public double Thickness { get; set; } = 3;

    /// <summary>Estilo usado pela ferramenta de esconder.</summary>
    public RedactionStyle RedactionStyle { get; set; } = RedactionStyle.Pixelar;

    /// <summary>Intensidade da ocultação.</summary>
    public int RedactionStrength { get; set; } = 12;

    /// <summary>Marcas colocadas até agora.</summary>
    public IReadOnlyList<AnnotationItem> Items => _items;

    /// <summary>Há algo para desfazer.</summary>
    public bool CanUndo => _items.Count > 0;

    /// <summary>Há algo para refazer.</summary>
    public bool CanRedo => _undone.Count > 0;

    /// <summary>Disparado quando a lista de marcas muda.</summary>
    public event EventHandler? ItemsChanged;

    /// <summary>Pedido de entrada de texto na posição indicada, em pixels da imagem.</summary>
    public event EventHandler<Point>? TextRequested;

    /// <summary>Fonte usada nas marcas de texto e nos números de passo.</summary>
    public void UseTypeface(Typeface typeface) => _typeface = typeface;

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Cabe na área disponível mantendo a proporção; nunca amplia além de 1:1, porque
        // ampliar uma captura só deixa o pixel grande e borrado.
        double scale = Math.Min(
            1.0,
            Math.Min(availableSize.Width / _source.Width, availableSize.Height / _source.Height));

        if (double.IsInfinity(scale) || double.IsNaN(scale) || scale <= 0)
        {
            scale = 1;
        }

        return new Size(_source.Width * scale, _source.Height * scale);
    }

    /// <summary>Fator entre pixel da imagem e unidade na tela.</summary>
    private double Scale => ActualWidth <= 0 ? 1 : ActualWidth / _source.Width;

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        context.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));

        // Desenhar em coordenadas da IMAGEM e escalar o contexto inteiro mantem as
        // marcas coerentes em qualquer zoom - e o mesmo codigo serve para a exportacao.
        context.PushTransform(new ScaleTransform(Scale, Scale));

        foreach (AnnotationItem item in _items)
        {
            if (item is RedactAnnotation redaction)
            {
                DrawRedactionPreview(context, redaction);
                continue;
            }

            AnnotationRenderer.Draw(context, item, _typeface, 1.0);
        }

        if (_dragging)
        {
            AnnotationItem? preview = BuildItem(_dragStart, _dragCurrent, committing: false);

            if (preview is RedactAnnotation previewRedaction)
            {
                DrawRedactionPreview(context, previewRedaction);
            }
            else if (preview is not null)
            {
                AnnotationRenderer.Draw(context, preview, _typeface, 1.0);
            }
        }

        context.Pop();
    }

    /// <summary>
    /// Prévia da área escondida.
    /// <para>
    /// No editor ela aparece como um bloco sólido com contorno tracejado — o contorno
    /// deixa claro <b>o que</b> está coberto, e não vai para o arquivo exportado. O
    /// resultado de verdade só é calculado na exportação, porque pixelar a cada
    /// movimento do mouse custaria caro à toa.
    /// </para>
    /// </summary>
    private void DrawRedactionPreview(DrawingContext context, RedactAnnotation redaction)
    {
        var fill = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1C));
        fill.Freeze();

        var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)), 1)
        {
            DashStyle = DashStyles.Dash,
        };
        edge.Freeze();

        context.DrawRectangle(fill, edge, redaction.Bounds);
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        Point at = ToImage(e.GetPosition(this));

        if (Tool == AnnotationTool.Text)
        {
            TextRequested?.Invoke(this, at);
            return;
        }

        _dragStart = at;
        _dragCurrent = at;
        _dragging = true;
        _freehand = Tool == AnnotationTool.Pen ? [at] : null;

        CaptureMouse();
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
        {
            return;
        }

        _dragCurrent = ToImage(e.GetPosition(this));
        _freehand?.Add(_dragCurrent);
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        _dragCurrent = ToImage(e.GetPosition(this));

        AnnotationItem? item = BuildItem(_dragStart, _dragCurrent, committing: true);
        if (item is not null)
        {
            Add(item);
        }

        _freehand = null;
        InvalidateVisual();
    }

    /// <summary>Acrescenta uma marca e limpa a pilha de refazer.</summary>
    public void Add(AnnotationItem item)
    {
        _items.Add(item);

        // Fazer algo novo apaga o caminho alternativo: refazer depois de uma acao nova
        // produziria uma mistura que o usuario nao pediu.
        _undone.Clear();

        ItemsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Desfaz a última marca.</summary>
    public void Undo()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _undone.Add(_items[^1]);
        _items.RemoveAt(_items.Count - 1);
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Refaz a última marca desfeita.</summary>
    public void Redo()
    {
        if (_undone.Count == 0)
        {
            return;
        }

        _items.Add(_undone[^1]);
        _undone.RemoveAt(_undone.Count - 1);
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Apaga todas as marcas.</summary>
    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        _undone.AddRange(_items);
        _items.Clear();
        ItemsChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Produz a imagem final, com as marcas aplicadas.</summary>
    public CapturedImage Export()
        => AnnotationRenderer.Flatten(_source, _items, _typeface);

    private AnnotationItem? BuildItem(Point from, Point to, bool committing)
    {
        var bounds = new Rect(
            Math.Min(from.X, to.X),
            Math.Min(from.Y, to.Y),
            Math.Abs(to.X - from.X),
            Math.Abs(to.Y - from.Y));

        // Arrasto minusculo e quase sempre um clique sem querer; so o passo numerado e
        // colocado por clique de verdade.
        bool tooSmall = bounds.Width < 4 && bounds.Height < 4;

        return Tool switch
        {
            AnnotationTool.Arrow when !tooSmall =>
                new ArrowAnnotation(from, to) { Color = Color, Thickness = Thickness },

            AnnotationTool.Rectangle when !tooSmall =>
                new RectangleAnnotation(bounds) { Color = Color, Thickness = Thickness },

            AnnotationTool.Ellipse when !tooSmall =>
                new EllipseAnnotation(bounds) { Color = Color, Thickness = Thickness },

            AnnotationTool.Highlight when !tooSmall =>
                new HighlightAnnotation(bounds) { Color = Color, Thickness = Thickness },

            AnnotationTool.Redact when !tooSmall =>
                new RedactAnnotation(bounds, RedactionStyle, RedactionStrength),

            AnnotationTool.Pen when _freehand is { Count: > 1 } =>
                new PenAnnotation([.. _freehand]) { Color = Color, Thickness = Thickness },

            AnnotationTool.Step when committing =>
                new StepAnnotation(from, NextStepNumber(), Math.Max(12, Thickness * 5))
                { Color = Color, Thickness = Thickness },

            _ => null,
        };
    }

    private int NextStepNumber() => _items.OfType<StepAnnotation>().Count() + 1;

    /// <summary>Converte um ponto da tela para pixel da imagem.</summary>
    private Point ToImage(Point onScreen)
    {
        double scale = Scale;
        return new Point(
            Math.Clamp(onScreen.X / scale, 0, _source.Width),
            Math.Clamp(onScreen.Y / scale, 0, _source.Height));
    }
}
