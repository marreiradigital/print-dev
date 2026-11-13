using System.Runtime.InteropServices;
using PrintDev.Core.Capture;
using PrintDev.Core.Screens;

namespace PrintDev.Tests.Capture;

public class FrozenScreenTests
{
    private const uint Outside = 0xFF0A0A0C;

    [Fact]
    public void Recorte_sai_identico_ao_da_captura_em_memoria()
    {
        // O seletor deixou de guardar a tela em array e recorta do bitmap. O resultado
        // tem de ser o mesmo, byte a byte, inclusive com origem fora do zero.
        CapturedImage image = Pattern(new PixelRect(-1366, 171, 40, 30));
        FrozenScreen frozen = FrozenScreen.FromImage(image);
        var region = new PixelRect(-1360, 180, 17, 11);

        CapturedImage expected = image.Crop(region);
        CapturedImage actual = frozen.Crop(region);

        Assert.Equal(expected.Bounds, actual.Bounds);
        Assert.Equal(expected.Pixels, actual.Pixels);
    }

    [Fact]
    public void Recorte_que_passa_da_borda_e_cortado_e_o_de_fora_e_recusado()
    {
        FrozenScreen frozen = FrozenScreen.FromImage(Pattern(new PixelRect(0, 0, 20, 10)));

        Assert.Equal(new PixelRect(15, 5, 5, 5), frozen.Crop(new PixelRect(15, 5, 50, 50)).Bounds);
        Assert.Throws<ArgumentException>(() => frozen.Crop(new PixelRect(100, 100, 5, 5)));
    }

    [Fact]
    public void Lupa_no_canto_mantem_o_pixel_do_cursor_no_centro()
    {
        // Cursor no pixel (0,0): metade do quadrado da lupa cai fora da tela. O centro
        // tem de continuar sendo exatamente o pixel do cursor, e o resto, vazio - antes,
        // o pedaco que sobrava era esticado e o centro apontava para outro pixel.
        CapturedImage image = Pattern(new PixelRect(0, 0, 20, 10));
        FrozenScreen frozen = FrozenScreen.FromImage(image);

        const int side = 5;
        var buffer = new byte[side * side * CapturedImage.BytesPerPixel];
        frozen.CopyInto(new PixelRect(-2, -2, side, side), buffer, Outside);

        Span<uint> words = MemoryMarshal.Cast<byte, uint>(buffer);
        uint cursor = MemoryMarshal.Cast<byte, uint>(image.Pixels)[0];

        Assert.Equal(cursor, words[(2 * side) + 2]);
        Assert.Equal(Outside, words[0]);
        Assert.Equal(Outside, words[(2 * side) + 1]);
        Assert.Equal(MemoryMarshal.Cast<byte, uint>(image.Pixels)[1], words[(2 * side) + 3]);
        Assert.Equal(MemoryMarshal.Cast<byte, uint>(image.Pixels)[20], words[(3 * side) + 2]);
    }

    [Fact]
    public void Lupa_no_meio_da_tela_copia_tudo_sem_vazio()
    {
        CapturedImage image = Pattern(new PixelRect(0, 0, 20, 10));
        FrozenScreen frozen = FrozenScreen.FromImage(image);

        var buffer = new byte[3 * 3 * CapturedImage.BytesPerPixel];
        frozen.CopyInto(new PixelRect(4, 4, 3, 3), buffer, Outside);

        Assert.Equal(image.Crop(new PixelRect(4, 4, 3, 3)).Pixels, buffer);
    }

    [Fact]
    public void Lupa_inteira_fora_da_tela_fica_vazia()
    {
        FrozenScreen frozen = FrozenScreen.FromImage(Pattern(new PixelRect(0, 0, 20, 10)));

        var buffer = new byte[3 * 3 * CapturedImage.BytesPerPixel];
        frozen.CopyInto(new PixelRect(50, 50, 3, 3), buffer, Outside);

        Assert.All(MemoryMarshal.Cast<byte, uint>(buffer).ToArray(), word => Assert.Equal(Outside, word));
    }

    [Fact]
    public void Leitura_de_cor_converte_bgra_para_rgb()
    {
        CapturedImage image = Pattern(new PixelRect(10, 20, 8, 8));
        FrozenScreen frozen = FrozenScreen.FromImage(image);

        int at = ((3 * 8) + 5) * CapturedImage.BytesPerPixel;
        System.Windows.Media.Color color = frozen.ReadPixel(15, 23);

        Assert.Equal(image.Pixels[at + 2], color.R);
        Assert.Equal(image.Pixels[at + 1], color.G);
        Assert.Equal(image.Pixels[at], color.B);
        Assert.Equal(System.Windows.Media.Colors.Black, frozen.ReadPixel(0, 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(1001)]
    public void Alfa_forcado_em_todos_os_pixels_sem_tocar_na_cor(int count)
    {
        // Tamanhos que nao fecham um vetor inteiro: a sobra precisa ser tratada tambem.
        var pixels = new byte[count * CapturedImage.BytesPerPixel];
        for (int index = 0; index < pixels.Length; index++)
        {
            pixels[index] = (byte)(index * 37);
        }

        byte[] original = (byte[])pixels.Clone();
        PixelOps.ForceOpaque(pixels);

        for (int index = 0; index < pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            Assert.Equal(original[index], pixels[index]);
            Assert.Equal(original[index + 1], pixels[index + 1]);
            Assert.Equal(original[index + 2], pixels[index + 2]);
            Assert.Equal(255, pixels[index + 3]);
        }
    }

    [Fact]
    public void Preenchimento_respeita_a_origem_e_corta_na_borda()
    {
        var bounds = new PixelRect(100, 50, 4, 3);
        var pixels = new byte[4 * 3 * CapturedImage.BytesPerPixel];

        PixelOps.Fill(pixels, bounds, new PixelRect(102, 51, 10, 10), blue: 1, green: 2, red: 3);

        Span<uint> words = MemoryMarshal.Cast<byte, uint>(pixels);
        const uint color = 0xFF030201;

        Assert.Equal(0u, words[0]);
        Assert.Equal(0u, words[(1 * 4) + 1]);
        Assert.Equal(color, words[(1 * 4) + 2]);
        Assert.Equal(color, words[(1 * 4) + 3]);
        Assert.Equal(color, words[(2 * 4) + 2]);
        Assert.Equal(0u, words[(2 * 4) + 1]);
    }

    private static CapturedImage Pattern(PixelRect bounds)
    {
        var pixels = new byte[bounds.Width * bounds.Height * CapturedImage.BytesPerPixel];

        for (int index = 0; index < pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            int pixel = index / CapturedImage.BytesPerPixel;
            pixels[index] = (byte)pixel;
            pixels[index + 1] = (byte)(pixel >> 8);
            pixels[index + 2] = (byte)(pixel * 3);
            pixels[index + 3] = 255;
        }

        return new CapturedImage(pixels, bounds);
    }
}
