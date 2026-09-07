using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintDev.Core.Capture;
using PrintDev.Core.Screens;

namespace PrintDev.Core.Annotation;

/// <summary>
/// Desenha as marcas e produz a imagem final.
/// </summary>
public static class AnnotationRenderer
{
    /// <summary>Proporção da cabeça da seta em relação à espessura do traço.</summary>
    private const double ArrowHeadFactor = 4.5;

    /// <summary>Desenha uma marca num contexto. Usado na tela e na exportação.</summary>
    public static void Draw(DrawingContext context, AnnotationItem item, Typeface typeface, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(item);

        var brush = new SolidColorBrush(item.Color);
        brush.Freeze();

        var pen = new Pen(brush, item.Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();

        switch (item)
        {
            case ArrowAnnotation arrow:
                DrawArrow(context, arrow, brush, pen);
                break;

            case RectangleAnnotation rectangle:
                context.DrawRectangle(null, pen, rectangle.Bounds);
                break;

            case EllipseAnnotation ellipse:
                context.DrawEllipse(
                    null, pen,
                    new Point(ellipse.Bounds.Left + (ellipse.Bounds.Width / 2),
                              ellipse.Bounds.Top + (ellipse.Bounds.Height / 2)),
                    ellipse.Bounds.Width / 2,
                    ellipse.Bounds.Height / 2);
                break;

            case PenAnnotation freehand when freehand.Points.Count > 1:
                DrawFreehand(context, freehand, pen);
                break;

            case HighlightAnnotation highlight:
                // Translucido para o conteudo continuar legivel por baixo - marca-texto
                // que esconde o texto nao e marca-texto.
                var wash = new SolidColorBrush(highlight.Color) { Opacity = 0.32 };
                wash.Freeze();
                context.DrawRectangle(wash, null, highlight.Bounds);
                break;

            case TextAnnotation text when !string.IsNullOrEmpty(text.Text):
                DrawText(context, text, brush, typeface, pixelsPerDip);
                break;

            case StepAnnotation step:
                DrawStep(context, step, brush, typeface, pixelsPerDip);
                break;

            // RedactAnnotation nao e desenhada: ela apaga pixel, e isso acontece antes,
            // direto no vetor de bytes da imagem.
        }
    }

    private static void DrawArrow(DrawingContext context, ArrowAnnotation arrow, Brush brush, Pen pen)
    {
        double head = Math.Max(8, arrow.Thickness * ArrowHeadFactor);
        Vector direction = arrow.To - arrow.From;
        double length = direction.Length;

        if (length < 1)
        {
            return;
        }

        direction /= length;

        // A haste para antes da ponta, senao o traco aparece por dentro da cabeca e
        // engrossa a ponta.
        Point shaftEnd = arrow.To - (direction * (head * 0.8));
        context.DrawLine(pen, arrow.From, shaftEnd);

        var normal = new Vector(-direction.Y, direction.X);
        var geometry = new StreamGeometry();

        using (StreamGeometryContext figure = geometry.Open())
        {
            figure.BeginFigure(arrow.To, isFilled: true, isClosed: true);
            figure.LineTo(arrow.To - (direction * head) + (normal * head * 0.45), true, true);
            figure.LineTo(arrow.To - (direction * head) - (normal * head * 0.45), true, true);
        }

        geometry.Freeze();
        context.DrawGeometry(brush, null, geometry);
    }

    private static void DrawFreehand(DrawingContext context, PenAnnotation freehand, Pen pen)
    {
        var geometry = new StreamGeometry();

        using (StreamGeometryContext figure = geometry.Open())
        {
            figure.BeginFigure(freehand.Points[0], isFilled: false, isClosed: false);
            figure.PolyLineTo([.. freehand.Points.Skip(1)], isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawText(
        DrawingContext context, TextAnnotation text, Brush brush, Typeface typeface, double pixelsPerDip)
    {
        var formatted = new FormattedText(
            text.Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            text.FontSize,
            brush,
            pixelsPerDip);

        // Contorno escuro por tras: texto colorido sobre captura arbitraria some com
        // facilidade, e o contorno garante leitura sobre qualquer fundo.
        Geometry outline = formatted.BuildGeometry(text.At);
        var halo = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 3)
        {
            LineJoin = PenLineJoin.Round,
        };
        halo.Freeze();

        context.DrawGeometry(null, halo, outline);
        context.DrawText(formatted, text.At);
    }

    private static void DrawStep(
        DrawingContext context, StepAnnotation step, Brush brush, Typeface typeface, double pixelsPerDip)
    {
        var outline = new Pen(new SolidColorBrush(Colors.White), Math.Max(2, step.Thickness * 0.6));
        outline.Freeze();

        context.DrawEllipse(brush, outline, step.Center, step.Radius, step.Radius);

        var formatted = new FormattedText(
            step.Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            step.Radius * 1.15,
            new SolidColorBrush(Colors.White),
            pixelsPerDip);

        context.DrawText(
            formatted,
            new Point(step.Center.X - (formatted.Width / 2), step.Center.Y - (formatted.Height / 2)));
    }

    /// <summary>
    /// Produz a imagem final: primeiro apaga o que tem de ser escondido, depois desenha
    /// as marcas por cima.
    /// <para>
    /// A ordem importa e não é negociável. Desenhar antes e apagar depois cobriria as
    /// próprias marcas; apagar depois de rasterizar tudo esconderia a marca mas deixaria
    /// o pixel original embaixo dela.
    /// </para>
    /// </summary>
    public static CapturedImage Flatten(
        CapturedImage source,
        IReadOnlyList<AnnotationItem> items,
        Typeface typeface,
        double pixelsPerDip = 1.0)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(items);

        // Trabalha numa copia: o original continua intacto para o caso de o usuario
        // desfazer tudo depois de exportar.
        var pixels = new byte[source.Pixels.Length];
        Buffer.BlockCopy(source.Pixels, 0, pixels, 0, pixels.Length);
        var working = new CapturedImage(pixels, source.Bounds);

        foreach (RedactAnnotation redaction in items.OfType<RedactAnnotation>())
        {
            Redaction.Apply(working, ToPixelRect(redaction.Bounds, source.Bounds), redaction.Style, redaction.Strength);
        }

        if (items.All(i => i is RedactAnnotation))
        {
            return working;
        }

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(working.ToBitmapSource(), new Rect(0, 0, working.Width, working.Height));

            foreach (AnnotationItem item in items)
            {
                Draw(context, item, typeface, pixelsPerDip);
            }
        }

        var target = new RenderTargetBitmap(
            working.Width, working.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();

        var converted = new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0);
        converted.Freeze();

        var flattened = new byte[working.Pixels.Length];
        converted.CopyPixels(flattened, working.Stride, 0);

        // Alfa opaco de novo: o RenderTargetBitmap trabalha em pre-multiplicado e pode
        // devolver alfa menor que 255, o que faria a imagem colada virar retangulo preto.
        for (int index = 3; index < flattened.Length; index += CapturedImage.BytesPerPixel)
        {
            flattened[index] = 255;
        }

        return new CapturedImage(flattened, source.Bounds);
    }

    /// <summary>Converte um retângulo do editor para a região da imagem.</summary>
    private static PixelRect ToPixelRect(Rect bounds, PixelRect imageBounds)
        => PixelRect.FromBounds(
            imageBounds.Left + (int)Math.Floor(bounds.Left),
            imageBounds.Top + (int)Math.Floor(bounds.Top),
            imageBounds.Left + (int)Math.Ceiling(bounds.Right),
            imageBounds.Top + (int)Math.Ceiling(bounds.Bottom));
}
