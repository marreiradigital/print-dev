using System.Windows;
using System.Windows.Controls;

namespace PrintDev.Controls;

/// <summary>
/// Empilha os filhos com espaçamento entre eles.
/// <para>
/// Existe porque o <see cref="StackPanel"/> do WPF <b>não tem</b> propriedade de
/// espaçamento, e o espaçamento é a regra inegociável do produto: nada pode ficar
/// colado. Sem um contêiner que resolva isso, o espaço volta a ser margem solta em cada
/// controle — e é assim que toda base de WPF apodrece, com <c>Margin="0,8,0,12"</c>
/// espalhada por dezenas de arquivos.
/// </para>
/// <para>
/// A alternativa mais comum, um estilo implícito que aplica margem nos filhos, foi
/// descartada por três motivos: vaza por herança para descendentes do mesmo tipo, dobra
/// a margem em aninhamento, e — o pior — <b>deixa um buraco fantasma quando um filho
/// está recolhido</b>, que é exatamente o caso dos ajustes que só aparecem quando o
/// ajuste-pai está ligado.
/// </para>
/// </summary>
public class Stack : Panel
{
    /// <summary>Direção do empilhamento.</summary>
    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(
            nameof(Orientation),
            typeof(Orientation),
            typeof(Stack),
            new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Espaço entre filhos visíveis.</summary>
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(
            nameof(Spacing),
            typeof(double),
            typeof(Stack),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <inheritdoc cref="OrientationProperty"/>
    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    /// <inheritdoc cref="SpacingProperty"/>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        bool vertical = Orientation == Orientation.Vertical;
        Size childConstraint = vertical
            ? new Size(availableSize.Width, double.PositiveInfinity)
            : new Size(double.PositiveInfinity, availableSize.Height);

        double along = 0;
        double across = 0;
        int visible = 0;

        foreach (UIElement child in InternalChildren)
        {
            // O ponto todo: filho recolhido nao ocupa espaco NEM gera espacamento.
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(childConstraint);

            along += vertical ? child.DesiredSize.Height : child.DesiredSize.Width;
            across = Math.Max(across, vertical ? child.DesiredSize.Width : child.DesiredSize.Height);
            visible++;
        }

        if (visible > 1)
        {
            along += Spacing * (visible - 1);
        }

        return vertical ? new Size(across, along) : new Size(along, across);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        bool vertical = Orientation == Orientation.Vertical;
        double offset = 0;
        bool first = true;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            if (!first)
            {
                offset += Spacing;
            }

            first = false;

            child.Arrange(vertical
                ? new Rect(0, offset, finalSize.Width, child.DesiredSize.Height)
                : new Rect(offset, 0, child.DesiredSize.Width, finalSize.Height));

            offset += vertical ? child.DesiredSize.Height : child.DesiredSize.Width;
        }

        return finalSize;
    }
}
