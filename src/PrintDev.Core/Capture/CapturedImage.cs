using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintDev.Core.Screens;

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
}
