using System.Windows;
using System.Windows.Media;
using PrintDev.Core.Annotation;
using PrintDev.Core.Capture;
using PrintDev.Core.Screens;

namespace PrintDev.Tests.Capture;

public class CropAnnotationTests
{
    private static readonly Typeface Font = new("Segoe UI");

    [Fact]
    public void Recorte_sozinho_devolve_so_a_area_escolhida()
    {
        CapturedImage source = Pattern(new PixelRect(100, 50, 40, 30));

        CapturedImage result = AnnotationRenderer.Flatten(source, [new CropAnnotation(new Rect(5, 4, 10, 8))], Font);

        Assert.Equal(new PixelRect(105, 54, 10, 8), result.Bounds);
        Assert.Equal(source.Crop(new PixelRect(105, 54, 10, 8)).Pixels, result.Pixels);
    }

    [Fact]
    public void Vale_o_ultimo_recorte()
    {
        // Recortar de novo troca a area, nao recorta o recorte: o primeiro so continua
        // valendo se o segundo for desfeito.
        CapturedImage source = Pattern(new PixelRect(0, 0, 40, 30));

        CapturedImage result = AnnotationRenderer.Flatten(
            source,
            [new CropAnnotation(new Rect(0, 0, 5, 5)), new CropAnnotation(new Rect(20, 10, 12, 6))],
            Font);

        Assert.Equal(new PixelRect(20, 10, 12, 6), result.Bounds);
    }

    [Fact]
    public void Recorte_que_passa_da_borda_fica_so_com_o_que_existe()
    {
        CapturedImage source = Pattern(new PixelRect(0, 0, 40, 30));

        CapturedImage result = AnnotationRenderer.Flatten(source, [new CropAnnotation(new Rect(30, 20, 50, 50))], Font);

        Assert.Equal(new PixelRect(30, 20, 10, 10), result.Bounds);
    }

    [Fact]
    public void Sem_recorte_a_imagem_continua_inteira()
    {
        CapturedImage source = Pattern(new PixelRect(0, 0, 40, 30));

        CapturedImage result = AnnotationRenderer.Flatten(source, [], Font);

        Assert.Equal(source.Bounds, result.Bounds);
        Assert.Equal(source.Pixels, result.Pixels);
    }

    [Fact]
    public void Marca_desenhada_antes_continua_dentro_do_recorte()
    {
        // O WPF so desenha numa thread STA; o xUnit roda em MTA.
        CapturedImage? result = null;
        var thread = new Thread(() =>
        {
            CapturedImage source = Solid(new PixelRect(0, 0, 40, 30));
            AnnotationItem mark = new RectangleAnnotation(new Rect(10, 10, 10, 10))
            {
                Color = Colors.Red,
                Thickness = 2,
            };

            result = AnnotationRenderer.Flatten(source, [mark, new CropAnnotation(new Rect(5, 5, 20, 20))], Font);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.NotNull(result);
        Assert.Equal(new PixelRect(5, 5, 20, 20), result.Bounds);

        // A borda esquerda do retangulo (x = 10 na imagem) fica em x = 5 no recorte.
        int at = ((10 - 5) * result.Stride) + ((10 - 5) * CapturedImage.BytesPerPixel);
        Assert.True(result.Pixels[at + 2] > 200, "a marca vermelha devia estar dentro do recorte");
        Assert.Equal(255, result.Pixels[at + 3]);
    }

    private static CapturedImage Pattern(PixelRect bounds)
    {
        var pixels = new byte[bounds.Width * bounds.Height * CapturedImage.BytesPerPixel];
        for (int index = 0; index < pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            pixels[index] = (byte)(index / 4);
            pixels[index + 1] = (byte)(index / 7);
            pixels[index + 2] = (byte)(index / 11);
            pixels[index + 3] = 255;
        }

        return new CapturedImage(pixels, bounds);
    }

    private static CapturedImage Solid(PixelRect bounds)
    {
        var pixels = new byte[bounds.Width * bounds.Height * CapturedImage.BytesPerPixel];
        for (int index = 3; index < pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            pixels[index] = 255;
        }

        return new CapturedImage(pixels, bounds);
    }
}
