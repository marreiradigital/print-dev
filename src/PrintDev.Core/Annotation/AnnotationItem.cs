using System.Windows;
using System.Windows.Media;
using PrintDev.Core.Configuration;

namespace PrintDev.Core.Annotation;

/// <summary>
/// Uma marca desenhada sobre a captura.
/// <para>
/// O modelo é vetorial e imutável: cada item é um <c>record</c>, e desfazer é remover o
/// último da lista. Guardar a marca como pixel obrigaria a manter uma cópia da imagem
/// inteira por passo — em captura de tela cheia, oito megabytes por traço.
/// </para>
/// <para>
/// As coordenadas estão em pixels da IMAGEM, não da tela: assim o desenho continua certo
/// quando o editor é redimensionado ou a imagem é vista com zoom.
/// </para>
/// </summary>
public abstract record AnnotationItem
{
    /// <summary>Cor do traço.</summary>
    public Color Color { get; init; } = Colors.Red;

    /// <summary>Espessura do traço, em pixels da imagem.</summary>
    public double Thickness { get; init; } = 3;
}

/// <summary>Seta, para apontar alguma coisa.</summary>
public sealed record ArrowAnnotation(Point From, Point To) : AnnotationItem;

/// <summary>Retângulo de contorno, para cercar uma área.</summary>
public sealed record RectangleAnnotation(Rect Bounds) : AnnotationItem;

/// <summary>Elipse de contorno.</summary>
public sealed record EllipseAnnotation(Rect Bounds) : AnnotationItem;

/// <summary>Traço à mão livre.</summary>
public sealed record PenAnnotation(IReadOnlyList<Point> Points) : AnnotationItem;

/// <summary>Marca-texto: retângulo preenchido e translúcido.</summary>
public sealed record HighlightAnnotation(Rect Bounds) : AnnotationItem;

/// <summary>Texto escrito sobre a imagem.</summary>
public sealed record TextAnnotation(Point At, string Text, double FontSize) : AnnotationItem;

/// <summary>
/// Número de passo, para explicar uma sequência. O número é atribuído na ordem em que
/// os círculos são colocados.
/// </summary>
public sealed record StepAnnotation(Point Center, int Number, double Radius) : AnnotationItem;

/// <summary>
/// Área escondida.
/// <para>
/// Diferente de todas as outras: as demais são desenhadas por cima na hora de exportar,
/// enquanto esta <b>apaga os pixels originais</b>. Continua na lista para poder ser
/// desfeita antes da exportação — depois dela, não há volta.
/// </para>
/// </summary>
public sealed record RedactAnnotation(Rect Bounds, RedactionStyle Style, int Strength) : AnnotationItem;
