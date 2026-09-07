using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PrintDev.Controls;

/// <summary>
/// Verdadeiro vira visível; falso, recolhido.
/// <para>
/// Recolhido, e não apenas invisível, porque o <see cref="Stack"/> pula filhos recolhidos
/// — é isso que evita o buraco fantasma onde um sub-ajuste está escondido.
/// </para>
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
