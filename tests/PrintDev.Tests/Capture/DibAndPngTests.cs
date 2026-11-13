using System.IO;
using System.Runtime.InteropServices;
using PrintDev.Core.Capture;
using PrintDev.Core.Clipboard;
using PrintDev.Core.Configuration;
using PrintDev.Core.Interop;
using PrintDev.Core.Screens;
using PrintDev.Core.Storage;

namespace PrintDev.Tests.Capture;

public class DibAndPngTests
{
    [Fact]
    public void Dib_escrito_direto_e_identico_ao_montado_em_array()
    {
        CapturedImage image = Gradient(7, 5);

        // O caminho da area de transferencia escreve direto na memoria do sistema; o
        // resultado tem de ser byte a byte o mesmo que o formato sempre foi.
        var v3 = new byte[DibBuilder.SizeV3(image)];
        DibBuilder.WriteV3(v3, image);
        Assert.Equal(DibBuilder.BuildV3(image), v3);

        var v5 = new byte[DibBuilder.SizeV5(image)];
        DibBuilder.WriteV5(v5, image);
        Assert.Equal(DibBuilder.BuildV5(image), v5);
    }

    [Fact]
    public void Dib_vai_de_baixo_para_cima_com_altura_positiva()
    {
        CapturedImage image = Gradient(3, 4);
        byte[] dib = DibBuilder.BuildV3(image);

        int headerSize = Marshal.SizeOf<BITMAPINFOHEADER>();
        Assert.Equal(headerSize + image.Pixels.Length, dib.Length);

        // Altura no deslocamento 8 do cabecalho: positiva significa de baixo para cima.
        Assert.Equal(4, BitConverter.ToInt32(dib, 8));

        // A primeira linha gravada e a ULTIMA da imagem.
        Assert.Equal(
            image.Pixels.AsSpan(3 * image.Stride, image.Stride).ToArray(),
            dib.AsSpan(headerSize, image.Stride).ToArray());

        Assert.Equal(
            image.Pixels.AsSpan(0, image.Stride).ToArray(),
            dib.AsSpan(headerSize + (3 * image.Stride), image.Stride).ToArray());
    }

    [Fact]
    public void Png_codificado_uma_vez_volta_com_os_mesmos_pixels()
    {
        CapturedImage image = Gradient(64, 33);
        ReadOnlyMemory<byte> png = ImageWriter.Encode(image, ImageFormat.Png, jpegQuality: 100);

        Assert.True(ImageWriter.IsPng(png.Span));

        string path = Path.Combine(Path.GetTempPath(), $"printdev-teste-{Guid.NewGuid():N}.png");

        try
        {
            ImageWriter.WriteAtomically(path, png.Span);

            Assert.Equal(png.ToArray(), File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));

            CapturedImage? back = CapturedImage.FromFile(path);
            Assert.NotNull(back);
            Assert.Equal(image.Width, back.Width);
            Assert.Equal(image.Height, back.Height);
            Assert.Equal(image.Pixels, back.Pixels);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Assinatura_de_png_nao_e_confundida()
    {
        Assert.False(ImageWriter.IsPng([]));
        Assert.False(ImageWriter.IsPng([0x89, 0x50, 0x4E, 0x47]));
        Assert.False(ImageWriter.IsPng([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46]));

        ReadOnlyMemory<byte> jpeg = ImageWriter.Encode(Gradient(8, 8), ImageFormat.Jpeg, jpegQuality: 90);
        Assert.False(ImageWriter.IsPng(jpeg.Span));
    }

    private static CapturedImage Gradient(int width, int height)
    {
        var pixels = new byte[width * height * CapturedImage.BytesPerPixel];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * CapturedImage.BytesPerPixel;
                pixels[at] = (byte)(x * 7);
                pixels[at + 1] = (byte)(y * 11);
                pixels[at + 2] = (byte)((x + y) * 3);
                pixels[at + 3] = 255;
            }
        }

        return new CapturedImage(pixels, new PixelRect(0, 0, width, height));
    }
}
