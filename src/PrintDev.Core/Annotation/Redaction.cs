using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.Screens;

namespace PrintDev.Core.Annotation;

/// <summary>
/// Esconde parte da imagem, apagando os pixels originais.
/// <para>
/// É a ferramenta que dá sentido ao produto para quem programa: token, chave de API,
/// e-mail de cliente e nome de banco de dados aparecem em captura de tela o tempo todo.
/// </para>
/// <para>
/// <b>A operação é destrutiva de propósito.</b> Desenhar um retângulo por cima esconde na
/// tela e não esconde no arquivo — qualquer editor separa as camadas de novo. Aqui os
/// bytes originais deixam de existir, e é isso que o aviso na interface promete.
/// </para>
/// </summary>
public static class Redaction
{
    /// <summary>Aplica o estilo pedido sobre a região.</summary>
    public static void Apply(CapturedImage image, PixelRect region, RedactionStyle style, int strength)
    {
        ArgumentNullException.ThrowIfNull(image);

        PixelRect area = region.Intersect(image.Bounds);
        if (area.IsEmpty)
        {
            return;
        }

        switch (style)
        {
            case RedactionStyle.Tarja:
                image.Fill(area, blue: 12, green: 12, red: 12);
                break;

            case RedactionStyle.Desfocar:
                Blur(image, area, Math.Clamp(strength, 2, 40));
                break;

            default:
                Pixelate(image, area, Math.Clamp(strength, 2, 80));
                break;
        }
    }

    /// <summary>
    /// Troca cada bloco pela cor média dele.
    /// <para>
    /// É o estilo padrão porque comunica melhor: o formato do texto some, mas continua
    /// visível que <b>havia</b> alguma coisa ali — diferente da tarja, que também pode ser
    /// lida como "espaço em branco".
    /// </para>
    /// </summary>
    public static void Pixelate(CapturedImage image, PixelRect region, int block)
    {
        ArgumentNullException.ThrowIfNull(image);

        PixelRect area = region.Intersect(image.Bounds);
        if (area.IsEmpty)
        {
            return;
        }

        int size = Math.Max(2, block);
        byte[] pixels = image.Pixels;
        int stride = image.Stride;

        for (int y = area.Top; y < area.Bottom; y += size)
        {
            int blockHeight = Math.Min(size, area.Bottom - y);

            for (int x = area.Left; x < area.Right; x += size)
            {
                int blockWidth = Math.Min(size, area.Right - x);
                long sumB = 0, sumG = 0, sumR = 0;
                int count = blockWidth * blockHeight;

                for (int row = 0; row < blockHeight; row++)
                {
                    int offset = Offset(image, x, y + row);

                    for (int column = 0; column < blockWidth; column++)
                    {
                        sumB += pixels[offset];
                        sumG += pixels[offset + 1];
                        sumR += pixels[offset + 2];
                        offset += CapturedImage.BytesPerPixel;
                    }
                }

                byte averageB = (byte)(sumB / count);
                byte averageG = (byte)(sumG / count);
                byte averageR = (byte)(sumR / count);

                for (int row = 0; row < blockHeight; row++)
                {
                    int offset = Offset(image, x, y + row);

                    for (int column = 0; column < blockWidth; column++)
                    {
                        pixels[offset] = averageB;
                        pixels[offset + 1] = averageG;
                        pixels[offset + 2] = averageR;
                        pixels[offset + 3] = 255;
                        offset += CapturedImage.BytesPerPixel;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Desfoque de caixa, aplicado duas vezes.
    /// <para>
    /// Duas passadas de caixa aproximam um desfoque gaussiano bem melhor que uma, e
    /// custam quase o mesmo — o desfoque de caixa é separável, então cada passada é
    /// linear no número de pixels e não no raio.
    /// </para>
    /// <para>
    /// Ainda assim, <b>desfoque é o estilo menos seguro dos três</b>: com raio pequeno,
    /// texto desfocado pode ser recuperado. Por isso o padrão é pixelar.
    /// </para>
    /// </summary>
    public static void Blur(CapturedImage image, PixelRect region, int radius)
    {
        ArgumentNullException.ThrowIfNull(image);

        PixelRect area = region.Intersect(image.Bounds);
        if (area.IsEmpty)
        {
            return;
        }

        int r = Math.Clamp(radius, 1, Math.Max(1, Math.Min(area.Width, area.Height) / 2));

        for (int pass = 0; pass < 2; pass++)
        {
            BoxBlurHorizontal(image, area, r);
            BoxBlurVertical(image, area, r);
        }
    }

    private static void BoxBlurHorizontal(CapturedImage image, PixelRect area, int radius)
    {
        byte[] pixels = image.Pixels;
        var line = new byte[area.Width * CapturedImage.BytesPerPixel];

        for (int y = area.Top; y < area.Bottom; y++)
        {
            Buffer.BlockCopy(pixels, Offset(image, area.Left, y), line, 0, line.Length);

            for (int x = 0; x < area.Width; x++)
            {
                int from = Math.Max(0, x - radius);
                int to = Math.Min(area.Width - 1, x + radius);
                int count = to - from + 1;
                int sumB = 0, sumG = 0, sumR = 0;

                for (int sample = from; sample <= to; sample++)
                {
                    int offset = sample * CapturedImage.BytesPerPixel;
                    sumB += line[offset];
                    sumG += line[offset + 1];
                    sumR += line[offset + 2];
                }

                int target = Offset(image, area.Left + x, y);
                pixels[target] = (byte)(sumB / count);
                pixels[target + 1] = (byte)(sumG / count);
                pixels[target + 2] = (byte)(sumR / count);
            }
        }
    }

    private static void BoxBlurVertical(CapturedImage image, PixelRect area, int radius)
    {
        byte[] pixels = image.Pixels;
        var column = new byte[area.Height * CapturedImage.BytesPerPixel];

        for (int x = area.Left; x < area.Right; x++)
        {
            for (int y = 0; y < area.Height; y++)
            {
                int source = Offset(image, x, area.Top + y);
                int target = y * CapturedImage.BytesPerPixel;
                column[target] = pixels[source];
                column[target + 1] = pixels[source + 1];
                column[target + 2] = pixels[source + 2];
            }

            for (int y = 0; y < area.Height; y++)
            {
                int from = Math.Max(0, y - radius);
                int to = Math.Min(area.Height - 1, y + radius);
                int count = to - from + 1;
                int sumB = 0, sumG = 0, sumR = 0;

                for (int sample = from; sample <= to; sample++)
                {
                    int offset = sample * CapturedImage.BytesPerPixel;
                    sumB += column[offset];
                    sumG += column[offset + 1];
                    sumR += column[offset + 2];
                }

                int target = Offset(image, x, area.Top + y);
                pixels[target] = (byte)(sumB / count);
                pixels[target + 1] = (byte)(sumG / count);
                pixels[target + 2] = (byte)(sumR / count);
            }
        }
    }

    private static int Offset(CapturedImage image, int x, int y)
        => ((y - image.Bounds.Top) * image.Stride)
           + ((x - image.Bounds.Left) * CapturedImage.BytesPerPixel);
}
