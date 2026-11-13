using System.Runtime.InteropServices;
using PrintDev.Core.Capture;
using PrintDev.Core.Interop;

namespace PrintDev.Core.Clipboard;

/// <summary>
/// Monta os bytes dos formatos de bitmap publicados na área de transferência.
/// </summary>
internal static class DibBuilder
{
    /// <summary>Compressão sem compressão nenhuma.</summary>
    private const uint BI_RGB = 0;

    /// <summary>As máscaras de canal vêm dentro do cabeçalho.</summary>
    private const uint BI_BITFIELDS = 3;

    /// <summary>Assinatura de sRGB, em ASCII invertido: "sRGB".</summary>
    private const uint LCS_sRGB = 0x73524742;

    /// <summary>Intenção de renderização adequada a fotografia e captura de tela.</summary>
    private const uint LCS_GM_IMAGES = 4;

    /// <summary>
    /// Monta o <c>CF_DIB</c>: cabeçalho de 40 bytes mais os pixels.
    /// <para>
    /// Sem <c>BITMAPFILEHEADER</c> — esse cabeçalho pertence ao arquivo <c>.bmp</c>, não
    /// ao formato da área de transferência. Incluí-lo faz o programa de destino ler os
    /// primeiros 14 bytes como se fossem pixels.
    /// </para>
    /// </summary>
    public static byte[] BuildV3(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] buffer = new byte[SizeV3(image)];
        WriteV3(buffer, image);
        return buffer;
    }

    /// <summary>Quantos bytes o <c>CF_DIB</c> desta imagem ocupa.</summary>
    public static int SizeV3(CapturedImage image)
        => Marshal.SizeOf<BITMAPINFOHEADER>() + image.Pixels.Length;

    /// <summary>
    /// Escreve o <c>CF_DIB</c> direto no destino — que, na prática, é a memória que vai
    /// para a área de transferência. Montar antes num array e copiar depois custava um
    /// bloco do tamanho da captura inteira a cada publicação.
    /// </summary>
    public static void WriteV3(Span<byte> destination, CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        int headerSize = Marshal.SizeOf<BITMAPINFOHEADER>();

        // Altura POSITIVA: de baixo para cima. E o arranjo que os programas antigos
        // esperam da area de transferencia; o de cima para baixo (altura negativa) e
        // mal suportado e aparece invertido em varios deles.
        BITMAPINFOHEADER header = BITMAPINFOHEADER.Create(image.Width, image.Height, topDown: false);
        MemoryMarshal.Write(destination, in header);

        CopyBottomUp(image, destination[headerSize..]);
    }

    /// <summary>
    /// Monta o <c>CF_DIBV5</c>: cabeçalho de 124 bytes com máscaras de canal, mais os
    /// pixels.
    /// <para>
    /// Este é o formato com canal alfa de verdade, e é justamente por isso que a captura
    /// força alfa 255 na origem: um alfa zerado aqui faria a imagem colada aparecer como
    /// um retângulo preto.
    /// </para>
    /// </summary>
    public static byte[] BuildV5(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] buffer = new byte[SizeV5(image)];
        WriteV5(buffer, image);
        return buffer;
    }

    /// <summary>Quantos bytes o <c>CF_DIBV5</c> desta imagem ocupa.</summary>
    public static int SizeV5(CapturedImage image)
        => Marshal.SizeOf<BITMAPV5HEADER>() + image.Pixels.Length;

    /// <summary>Escreve o <c>CF_DIBV5</c> direto no destino, pelo mesmo motivo do V3.</summary>
    public static void WriteV5(Span<byte> destination, CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        int headerSize = Marshal.SizeOf<BITMAPV5HEADER>();

        var header = new BITMAPV5HEADER
        {
            Size = (uint)headerSize,
            Width = image.Width,
            Height = image.Height, // positiva: de baixo para cima
            Planes = 1,
            BitCount = 32,
            Compression = BI_BITFIELDS,
            SizeImage = (uint)image.Pixels.Length,

            // Ordem de bytes BGRA em memoria, que lido como inteiro de 32 bits em
            // maquina little-endian da 0xAARRGGBB.
            RedMask = 0x00FF0000,
            GreenMask = 0x0000FF00,
            BlueMask = 0x000000FF,
            AlphaMask = 0xFF000000,

            CSType = LCS_sRGB,
            Intent = LCS_GM_IMAGES,
        };

        MemoryMarshal.Write(destination, in header);
        CopyBottomUp(image, destination[headerSize..]);
    }

    /// <summary>
    /// Copia os pixels invertendo a ordem das linhas, de cima para baixo para de baixo
    /// para cima.
    /// </summary>
    private static void CopyBottomUp(CapturedImage image, Span<byte> destination)
    {
        int stride = image.Stride;
        ReadOnlySpan<byte> source = image.Pixels;

        for (int row = 0; row < image.Height; row++)
        {
            source.Slice(row * stride, stride)
                  .CopyTo(destination.Slice((image.Height - 1 - row) * stride, stride));
        }
    }
}
