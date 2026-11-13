using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintDev.Core.Screens;
using PrintDev.Core.Storage;

namespace PrintDev.Core.Capture;

/// <summary>
/// Uma captura já em memória: pixels crus em BGRA, de cima para baixo.
/// <para>
/// Guardar os bytes crus, e não um objeto de imagem pronto, é decisão de projeto. Os
/// mesmos bytes alimentam três consumidores com exigências diferentes: o codificador
/// que grava o arquivo, o formato <c>CF_DIB</c> da área de transferência (que quer o
/// arranjo invertido) e o desenho na tela. Converter para um objeto de imagem no meio
/// do caminho obrigaria a desfazer a conversão depois.
/// </para>
/// </summary>
public sealed class CapturedImage
{
    /// <summary>Quatro bytes por pixel: azul, verde, vermelho, alfa.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>
    /// Cria a captura a partir dos bytes.
    /// </summary>
    /// <param name="pixels">BGRA, de cima para baixo, sem preenchimento entre linhas.</param>
    /// <param name="bounds">Onde ela estava no desktop virtual, em pixels físicos.</param>
    public CapturedImage(byte[] pixels, PixelRect bounds)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        if (bounds.IsEmpty)
        {
            throw new ArgumentException("A captura não pode ter área zero.", nameof(bounds));
        }

        int expected = bounds.Width * bounds.Height * BytesPerPixel;
        if (pixels.Length != expected)
        {
            throw new ArgumentException(
                $"A quantidade de bytes ({pixels.Length}) não bate com {bounds} ({expected}).",
                nameof(pixels));
        }

        Pixels = pixels;
        Bounds = bounds;
    }

    /// <summary>Pixels em BGRA, de cima para baixo.</summary>
    public byte[] Pixels { get; }

    /// <summary>Posição e tamanho originais no desktop virtual.</summary>
    public PixelRect Bounds { get; }

    /// <summary>Largura em pixels.</summary>
    public int Width => Bounds.Width;

    /// <summary>Altura em pixels.</summary>
    public int Height => Bounds.Height;

    /// <summary>Bytes por linha.</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>
    /// Recorta uma parte, em coordenadas do desktop virtual.
    /// </summary>
    /// <exception cref="ArgumentException">Quando o recorte não intersecta a captura.</exception>
    public CapturedImage Crop(PixelRect region)
    {
        PixelRect clipped = region.Intersect(Bounds);
        if (clipped.IsEmpty)
        {
            throw new ArgumentException($"O recorte {region} está fora de {Bounds}.", nameof(region));
        }

        if (clipped == Bounds)
        {
            return this;
        }

        var cropped = new byte[clipped.Width * clipped.Height * BytesPerPixel];
        int sourceOffsetX = (clipped.Left - Bounds.Left) * BytesPerPixel;
        int lineBytes = clipped.Width * BytesPerPixel;

        for (int row = 0; row < clipped.Height; row++)
        {
            int sourceRow = clipped.Top - Bounds.Top + row;
            Buffer.BlockCopy(
                Pixels,
                (sourceRow * Stride) + sourceOffsetX,
                cropped,
                row * lineBytes,
                lineBytes);
        }

        return new CapturedImage(cropped, clipped);
    }

    /// <summary>
    /// Reduz a imagem para caber num quadrado de <paramref name="maxSide"/> pixels,
    /// mantendo a proporção. Nunca amplia: imagem que já cabe volta ela mesma.
    /// <para>
    /// Cada pixel do resultado é a <b>média de toda a área</b> que ele cobre na origem,
    /// com peso fracionário nas bordas. É o filtro que não perde detalhe nem cria
    /// serrilhado ao reduzir muito — texto de terminal reduzido continua legível como
    /// mancha, em vez de virar ruído.
    /// </para>
    /// <para>
    /// Feito aqui, sobre os bytes, e não com <c>TransformedBitmap</c>: aquele é
    /// preguiçoso e guarda a imagem de origem inteira para reduzir na hora de desenhar.
    /// Uma "miniatura" do histórico segurava assim a captura completa em memória nativa.
    /// </para>
    /// </summary>
    public CapturedImage Downscale(int maxSide)
    {
        if (maxSide < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSide), maxSide, "O lado máximo precisa ser positivo.");
        }

        double factor = (double)maxSide / Math.Max(Width, Height);
        if (factor >= 1.0)
        {
            return this;
        }

        int targetWidth = Math.Max(1, (int)Math.Round(Width * factor));
        int targetHeight = Math.Max(1, (int)Math.Round(Height * factor));

        double spanX = (double)Width / targetWidth;
        double spanY = (double)Height / targetHeight;
        double area = spanX * spanY;

        var result = new byte[targetWidth * targetHeight * BytesPerPixel];

        // Uma linha acumulada por vez, e nao a imagem intermediaria inteira: a memoria
        // extra fica do tamanho de uma linha da origem, seja qual for a captura.
        float[] row = System.Buffers.ArrayPool<float>.Shared.Rent(Width * BytesPerPixel);

        try
        {
            for (int targetY = 0; targetY < targetHeight; targetY++)
            {
                Array.Clear(row, 0, Width * BytesPerPixel);

                double top = targetY * spanY;
                double bottom = top + spanY;
                int firstRow = (int)top;
                int lastRow = Math.Min(Height - 1, (int)Math.Ceiling(bottom) - 1);

                for (int sourceY = firstRow; sourceY <= lastRow; sourceY++)
                {
                    float weight = (float)(Math.Min(sourceY + 1, bottom) - Math.Max(sourceY, top));
                    if (weight <= 0)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> source = Pixels.AsSpan(sourceY * Stride, Stride);
                    for (int index = 0; index < source.Length; index++)
                    {
                        row[index] += source[index] * weight;
                    }
                }

                int output = targetY * targetWidth * BytesPerPixel;

                for (int targetX = 0; targetX < targetWidth; targetX++)
                {
                    double left = targetX * spanX;
                    double right = left + spanX;
                    int firstColumn = (int)left;
                    int lastColumn = Math.Min(Width - 1, (int)Math.Ceiling(right) - 1);

                    float blue = 0, green = 0, red = 0, alpha = 0;

                    for (int sourceX = firstColumn; sourceX <= lastColumn; sourceX++)
                    {
                        float weight = (float)(Math.Min(sourceX + 1, right) - Math.Max(sourceX, left));
                        if (weight <= 0)
                        {
                            continue;
                        }

                        int at = sourceX * BytesPerPixel;
                        blue += row[at] * weight;
                        green += row[at + 1] * weight;
                        red += row[at + 2] * weight;
                        alpha += row[at + 3] * weight;
                    }

                    result[output] = ToByte(blue / area);
                    result[output + 1] = ToByte(green / area);
                    result[output + 2] = ToByte(red / area);
                    result[output + 3] = ToByte(alpha / area);
                    output += BytesPerPixel;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(row);
        }

        return new CapturedImage(result, new PixelRect(Bounds.Left, Bounds.Top, targetWidth, targetHeight));
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    /// <summary>
    /// Preenche uma área com preto opaco. Serve para apagar os buracos do desktop
    /// virtual, onde o Windows devolve lixo por não haver monitor nenhum.
    /// </summary>
    public void Fill(PixelRect region, byte blue = 0, byte green = 0, byte red = 0)
    {
        PixelRect clipped = region.Intersect(Bounds);
        if (clipped.IsEmpty)
        {
            return;
        }

        for (int row = 0; row < clipped.Height; row++)
        {
            int offset = ((clipped.Top - Bounds.Top + row) * Stride)
                         + ((clipped.Left - Bounds.Left) * BytesPerPixel);

            for (int column = 0; column < clipped.Width; column++)
            {
                Pixels[offset] = blue;
                Pixels[offset + 1] = green;
                Pixels[offset + 2] = red;
                Pixels[offset + 3] = 255;
                offset += BytesPerPixel;
            }
        }
    }

    /// <summary>
    /// Verdadeiro quando todos os pixels são pretos.
    /// <para>
    /// Serve para avisar o usuário em vez de entregar um retângulo preto sem explicação:
    /// conteúdo protegido contra cópia (vídeo com DRM, janela marcada para não aparecer
    /// em captura) e jogo em tela cheia exclusiva vêm assim, e não há como contornar de
    /// forma legítima.
    /// </para>
    /// </summary>
    public bool IsCompletelyBlack()
    {
        for (int index = 0; index < Pixels.Length; index += BytesPerPixel)
        {
            if (Pixels[index] != 0 || Pixels[index + 1] != 0 || Pixels[index + 2] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Converte para uma imagem do WPF, já congelada para poder cruzar threads.
    /// </summary>
    /// <remarks>
    /// A resolução declarada é 96 pontos por polegada de propósito: os bytes estão em
    /// pixels físicos, e 96 faz o WPF tratar um pixel como uma unidade, sem reescalar.
    /// </remarks>
    public BitmapSource ToBitmapSource()
    {
        BitmapSource source = BitmapSource.Create(
            Width,
            Height,
            dpiX: 96,
            dpiY: 96,
            PixelFormats.Bgra32,
            palette: null,
            Pixels,
            Stride);

        source.Freeze();
        return source;
    }

    /// <summary>
    /// Recarrega uma captura a partir de um arquivo em disco.
    /// <para>
    /// O histórico guarda só miniaturas — vinte capturas de tela cheia em memória
    /// passariam de cem megabytes. Quando o usuário pede a imagem de volta, ela vem do
    /// arquivo, que é onde ela está inteira.
    /// </para>
    /// </summary>
    /// <returns><see langword="null"/> quando o arquivo sumiu ou não deu para ler.</returns>
    public static CapturedImage? FromFile(string path) => FromFile(path, out _);

    /// <summary>
    /// Recarrega uma captura e devolve também os bytes do arquivo quando eles já são um
    /// PNG — que é exatamente o que a área de transferência publica primeiro.
    /// <para>
    /// Recopiar do histórico ou forçar a colagem como imagem reaproveita assim o próprio
    /// arquivo, em vez de codificar de novo a mesma imagem que acabou de ser lida dele.
    /// </para>
    /// </summary>
    /// <param name="path">O arquivo.</param>
    /// <param name="png">Os bytes do arquivo, se for PNG; vazio nos outros casos.</param>
    public static CapturedImage? FromFile(string path, out ReadOnlyMemory<byte> png)
    {
        png = default;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            // Os bytes sao lidos de uma vez e o arquivo fica livre na hora: segura-lo
            // aberto faria apagar ou desfazer a captura falhar.
            byte[] encoded = File.ReadAllBytes(path);

            CapturedImage? image = Decode(encoded);

            if (image is not null && ImageWriter.IsPng(encoded))
            {
                png = encoded;
            }

            return image;
        }
        catch (Exception)
        {
            // Arquivo corrompido, formato inesperado, permissao negada: quem chamou
            // decide o que fazer com o nulo. Nada disso justifica derrubar o programa.
            return null;
        }
    }

    /// <summary>
    /// Decodifica uma imagem já em memória para BGRA opaco.
    /// <para>
    /// Sem cache do decodificador: os pixels são decodificados direto para o array
    /// final. Com o cache, o WPF guardava antes uma cópia nativa da imagem inteira, só
    /// para ela ser copiada de novo em seguida.
    /// </para>
    /// </summary>
    private static CapturedImage? Decode(byte[] encoded)
    {
        using var stream = new MemoryStream(encoded, writable: false);

        BitmapDecoder decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.None);

        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        BitmapSource frame = decoder.Frames[0];
        BitmapSource converted = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * BytesPerPixel;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        // O arquivo pode ter vindo de qualquer lugar e trazer alfa. As mesmas razoes
        // da captura valem aqui: alfa zerado vira retangulo preto ao colar.
        for (int index = 3; index < pixels.Length; index += BytesPerPixel)
        {
            pixels[index] = 255;
        }

        return new CapturedImage(pixels, new PixelRect(0, 0, width, height));
    }
}
