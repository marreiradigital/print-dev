using PrintDev.Core.Capture;
using PrintDev.Core.Screens;

namespace PrintDev.Tests.Capture;

public class CapturedImageDownscaleTests
{
    [Fact]
    public void Imagem_que_ja_cabe_volta_ela_mesma()
    {
        CapturedImage image = Solid(100, 60, 10, 20, 30);

        // Ampliar uma captura so deixa o pixel grande e borrado, e copiar sem
        // necessidade gastaria memoria a toa.
        Assert.Same(image, image.Downscale(160));
        Assert.Same(image, image.Downscale(100));
    }

    [Fact]
    public void Reducao_mantem_a_proporcao_pelo_lado_maior()
    {
        CapturedImage thumbnail = Solid(3360, 1080, 0, 0, 0).Downscale(160);

        Assert.Equal(160, thumbnail.Width);
        Assert.Equal(51, thumbnail.Height);
        Assert.Equal(160 * 51 * CapturedImage.BytesPerPixel, thumbnail.Pixels.Length);
    }

    [Fact]
    public void Lado_menor_nunca_some()
    {
        // Uma faixa de 4000x3 reduzida a 100 daria altura zero sem o piso.
        CapturedImage thumbnail = Solid(4000, 3, 0, 0, 0).Downscale(100);

        Assert.Equal(100, thumbnail.Width);
        Assert.Equal(1, thumbnail.Height);
    }

    [Fact]
    public void Cor_lisa_continua_exatamente_a_mesma()
    {
        CapturedImage thumbnail = Solid(1913, 1075, 236, 0, 140).Downscale(160);

        for (int index = 0; index < thumbnail.Pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            Assert.Equal(236, thumbnail.Pixels[index]);
            Assert.Equal(0, thumbnail.Pixels[index + 1]);
            Assert.Equal(140, thumbnail.Pixels[index + 2]);
            Assert.Equal(255, thumbnail.Pixels[index + 3]);
        }
    }

    [Fact]
    public void Cada_pixel_e_a_media_da_area_que_cobre()
    {
        // Xadrez de 1 pixel preto e branco: reduzido a metade, cada pixel cobre dois
        // pretos e dois brancos e tem de sair cinza medio. Pegar um pixel so (vizinho
        // mais proximo) daria preto ou branco puro, que e o serrilhado que o filtro evita.
        var pixels = new byte[4 * 4 * CapturedImage.BytesPerPixel];
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                byte value = (x + y) % 2 == 0 ? (byte)255 : (byte)0;
                int at = ((y * 4) + x) * CapturedImage.BytesPerPixel;
                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        CapturedImage half = new CapturedImage(pixels, new PixelRect(0, 0, 4, 4)).Downscale(2);

        Assert.Equal(2, half.Width);
        Assert.Equal(2, half.Height);

        for (int index = 0; index < half.Pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            Assert.InRange(half.Pixels[index], 127, 128);
            Assert.Equal(255, half.Pixels[index + 3]);
        }
    }

    [Fact]
    public void Fator_fracionario_pesa_as_bordas()
    {
        // Tres colunas (preto, branco, preto) reduzidas a duas: cada pixel do resultado
        // cobre uma coluna e meia. A esquerda pega 1 preto + meio branco = 1/3 de branco.
        var pixels = new byte[3 * 1 * CapturedImage.BytesPerPixel];
        pixels[4] = pixels[5] = pixels[6] = 255;
        pixels[3] = pixels[7] = pixels[11] = 255;

        CapturedImage reduced = new CapturedImage(pixels, new PixelRect(0, 0, 3, 1)).Downscale(2);

        Assert.Equal(2, reduced.Width);
        Assert.Equal(85, reduced.Pixels[0]);
        Assert.Equal(85, reduced.Pixels[4]);
    }

    [Fact]
    public void Lado_maximo_invalido_e_recusado()
    {
        CapturedImage image = Solid(10, 10, 0, 0, 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => image.Downscale(0));
    }

    private static CapturedImage Solid(int width, int height, byte blue, byte green, byte red)
    {
        var pixels = new byte[width * height * CapturedImage.BytesPerPixel];

        for (int index = 0; index < pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            pixels[index] = blue;
            pixels[index + 1] = green;
            pixels[index + 2] = red;
            pixels[index + 3] = 255;
        }

        return new CapturedImage(pixels, new PixelRect(0, 0, width, height));
    }
}
