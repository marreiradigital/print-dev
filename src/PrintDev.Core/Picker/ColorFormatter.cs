using System.Globalization;
using PrintDev.Core.Configuration;

// O namespace NAO se chama Colors: isso sombrearia a classe System.Windows.Media.Colors
// em todo arquivo do Core que importe este espaco, e o erro sai como "White nao existe no
// namespace", que nao aponta para a causa.
namespace PrintDev.Core.Picker;

/// <summary>
/// Escreve uma cor no formato que o usuário escolheu.
/// <para>
/// Lógica pura e testada: é texto que vai direto para uma folha de estilo ou para o
/// código, e um arredondamento errado aqui vira uma cor visivelmente diferente lá.
/// </para>
/// </summary>
public static class ColorFormatter
{
    /// <summary>Formata a cor conforme as configurações.</summary>
    public static string Format(byte r, byte g, byte b, ColorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Format(r, g, b, settings.Format, settings.UppercaseHex);
    }

    /// <summary>Formata a cor no formato indicado.</summary>
    public static string Format(byte r, byte g, byte b, ColorFormat format, bool uppercase = false)
    {
        string hex = format switch
        {
            ColorFormat.Hex0X => "0x" + Hex(r, g, b),
            ColorFormat.Rgb => $"rgb({r}, {g}, {b})",
            ColorFormat.Hsl => ToHsl(r, g, b),
            _ => "#" + Hex(r, g, b),
        };

        // Maiuscula so afeta os digitos hexadecimais; rgb() e hsl() nao tem letra.
        return format is ColorFormat.Hex or ColorFormat.Hex0X && uppercase
            ? hex.ToUpperInvariant().Replace("0X", "0x", StringComparison.Ordinal)
            : hex;
    }

    private static string Hex(byte r, byte g, byte b)
        => $"{r:x2}{g:x2}{b:x2}";

    /// <summary>
    /// Converte para matiz, saturação e luminosidade — o formato que CSS usa quando se
    /// quer variar uma cor sem trocá-la.
    /// </summary>
    private static string ToHsl(byte r, byte g, byte b)
    {
        double red = r / 255.0;
        double green = g / 255.0;
        double blue = b / 255.0;

        double max = Math.Max(red, Math.Max(green, blue));
        double min = Math.Min(red, Math.Min(green, blue));
        double lightness = (max + min) / 2;
        double hue = 0;
        double saturation = 0;

        if (max - min > 0.00001)
        {
            double delta = max - min;

            // Abaixo de meio, o divisor e a soma; acima, o que falta para dois. E o que
            // faz a saturacao chegar a 100% tanto no muito claro quanto no muito escuro.
            saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);

            if (Math.Abs(max - red) < 0.00001)
            {
                hue = ((green - blue) / delta) + (green < blue ? 6 : 0);
            }
            else if (Math.Abs(max - green) < 0.00001)
            {
                hue = ((blue - red) / delta) + 2;
            }
            else
            {
                hue = ((red - green) / delta) + 4;
            }

            hue *= 60;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"hsl({Math.Round(hue)}, {Math.Round(saturation * 100)}%, {Math.Round(lightness * 100)}%)");
    }
}
