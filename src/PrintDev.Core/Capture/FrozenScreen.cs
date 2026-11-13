using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintDev.Core.Screens;

namespace PrintDev.Core.Capture;

/// <summary>
/// A tela congelada no instante em que o seletor abriu.
/// <para>
/// Existe <b>uma cópia só</b> dos pixels, e ela é o bitmap nativo que o seletor desenha.
/// O recorte final e a leitura da lupa saem dele direto. A versão anterior mantinha
/// também um array gerenciado com a mesma tela inteira — em dois monitores, 14 MB a mais
/// a cada abertura, num bloco que só o coletor completo devolve.
/// </para>
/// </summary>
public sealed class FrozenScreen
{
    private FrozenScreen(BitmapSource bitmap, PixelRect bounds)
    {
        Bitmap = bitmap;
        Bounds = bounds;
    }

    /// <summary>O bitmap, já congelado, em BGRA a 96 pontos por polegada.</summary>
    public BitmapSource Bitmap { get; }

    /// <summary>Onde a imagem está no desktop virtual, em pixels físicos.</summary>
    public PixelRect Bounds { get; }

    /// <summary>
    /// Cria a partir de pixels BGRA de cima para baixo, copiando-os uma vez para o
    /// bitmap nativo.
    /// </summary>
    internal static unsafe FrozenScreen FromPixels(ReadOnlySpan<byte> pixels, PixelRect bounds)
    {
        int stride = bounds.Width * CapturedImage.BytesPerPixel;

        if (pixels.Length != stride * bounds.Height)
        {
            throw new ArgumentException("A quantidade de bytes não bate com a área.", nameof(pixels));
        }

        fixed (byte* start = pixels)
        {
            BitmapSource bitmap = BitmapSource.Create(
                bounds.Width,
                bounds.Height,
                dpiX: 96,
                dpiY: 96,
                PixelFormats.Bgra32,
                palette: null,
                (IntPtr)start,
                pixels.Length,
                stride);

            bitmap.Freeze();
            return new FrozenScreen(bitmap, bounds);
        }
    }

    /// <summary>Cria a partir de uma captura já em memória. Serve aos testes.</summary>
    internal static FrozenScreen FromImage(CapturedImage image) => FromPixels(image.Pixels, image.Bounds);

    /// <summary>
    /// Recorta uma parte, em coordenadas do desktop virtual.
    /// </summary>
    /// <exception cref="ArgumentException">Quando o recorte não intersecta a tela.</exception>
    public CapturedImage Crop(PixelRect region)
    {
        PixelRect clipped = region.Intersect(Bounds);
        if (clipped.IsEmpty)
        {
            throw new ArgumentException($"O recorte {region} está fora de {Bounds}.", nameof(region));
        }

        int stride = clipped.Width * CapturedImage.BytesPerPixel;
        var pixels = new byte[stride * clipped.Height];

        Bitmap.CopyPixels(ToLocal(clipped), pixels, stride, 0);
        return new CapturedImage(pixels, clipped);
    }

    /// <summary>
    /// Copia um quadrado do desktop virtual para <paramref name="destination"/>, que tem
    /// exatamente o tamanho de <paramref name="region"/>. O que cai fora da tela recebe
    /// <paramref name="outside"/>.
    /// <para>
    /// É o que a lupa usa. A posição de cada pixel no destino é sempre a mesma em
    /// relação ao centro, inclusive na borda da tela — e é isso que mantém a célula
    /// destacada em cima do pixel que está de fato sob o cursor.
    /// </para>
    /// </summary>
    public unsafe void CopyInto(PixelRect region, byte[] destination, uint outside)
    {
        ArgumentNullException.ThrowIfNull(destination);

        int stride = region.Width * CapturedImage.BytesPerPixel;
        if (destination.Length != stride * region.Height)
        {
            throw new ArgumentException("O destino precisa ter o tamanho exato da região.", nameof(destination));
        }

        PixelRect inside = region.Intersect(Bounds);

        if (inside != region)
        {
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(destination).Fill(outside);
        }

        if (inside.IsEmpty)
        {
            return;
        }

        int offset = ((inside.Top - region.Top) * stride)
                     + ((inside.Left - region.Left) * CapturedImage.BytesPerPixel);

        fixed (byte* start = destination)
        {
            Bitmap.CopyPixels(ToLocal(inside), (IntPtr)(start + offset), destination.Length - offset, stride);
        }
    }

    /// <summary>A cor de um pixel do desktop virtual. Preto fora da tela.</summary>
    public unsafe Color ReadPixel(int x, int y)
    {
        if (!Bounds.Contains(x, y))
        {
            return Colors.Black;
        }

        uint pixel = 0;
        Bitmap.CopyPixels(ToLocal(new PixelRect(x, y, 1, 1)), (IntPtr)(&pixel), sizeof(uint), sizeof(uint));

        // BGRA lido como inteiro little-endian: 0xAARRGGBB.
        return Color.FromRgb((byte)(pixel >> 16), (byte)(pixel >> 8), (byte)pixel);
    }

    private Int32Rect ToLocal(PixelRect rect)
        => new(rect.Left - Bounds.Left, rect.Top - Bounds.Top, rect.Width, rect.Height);
}
