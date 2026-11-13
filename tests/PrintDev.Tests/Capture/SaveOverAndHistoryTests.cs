using System.IO;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.History;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Screens;
using PrintDev.Core.Storage;

namespace PrintDev.Tests.Capture;

public sealed class SaveOverAndHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"printdev-teste-{Guid.NewGuid():N}");

    public SaveOverAndHistoryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Salvar_do_editor_regrava_o_proprio_arquivo_em_png()
    {
        string path = Path.Combine(_root, "captura.png");
        File.WriteAllBytes(path, ImageWriter.EncodePng(Solid(4, 4, 0)).ToArray());

        CaptureResult result = Pipeline().SaveOver(Solid(6, 3, 200), path);

        Assert.Equal(path, result.SavedPath);
        Assert.False(result.Png.IsEmpty);
        Assert.Single(Directory.GetFiles(_root));

        CapturedImage? back = CapturedImage.FromFile(path);
        Assert.NotNull(back);
        Assert.Equal(6, back.Width);
        Assert.Equal(200, back.Pixels[0]);
    }

    [Fact]
    public void Arquivo_jpeg_continua_jpeg_ao_ser_regravado()
    {
        string path = Path.Combine(_root, "captura.jpg");
        File.WriteAllBytes(path, ImageWriter.Encode(Solid(4, 4, 0), ImageFormat.Jpeg, 90).ToArray());

        CaptureResult result = Pipeline().SaveOver(Solid(8, 8, 128), path);

        // Gravar PNG dentro de um .jpg enganaria qualquer programa que confie na extensao.
        Assert.True(result.Png.IsEmpty);
        Assert.False(ImageWriter.IsPng(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Arquivo_que_sumiu_vira_captura_nova_em_vez_de_perder_a_anotacao()
    {
        // No lugar da pasta do arquivo agora ha um arquivo comum: regravar falha, e o
        // resultado tem de ser uma captura nova, e nao a anotacao perdida.
        File.WriteAllText(Path.Combine(_root, "pasta-que-virou-arquivo"), "x");
        string gone = Path.Combine(_root, "pasta-que-virou-arquivo", "captura.png");

        CaptureResult result = Pipeline().SaveOver(Solid(2, 2, 1), gone);

        Assert.NotNull(result.SavedPath);
        Assert.NotEqual(gone, result.SavedPath);
        Assert.True(File.Exists(result.SavedPath));
    }

    [Fact]
    public void Regravar_nao_duplica_a_entrada_no_historico()
    {
        var history = new CaptureHistory(new FixedSettings(new AppSettings()));

        history.Add(Solid(4, 4, 0), @"C:\capturas\a.png");
        history.Add(Solid(4, 4, 0), @"C:\capturas\b.png");
        history.Add(Solid(4, 4, 9), @"C:\CAPTURAS\A.png");

        Assert.Equal(2, history.Items.Count);
        Assert.Equal(@"C:\CAPTURAS\A.png", history.Items[0].Path);
        Assert.Equal(@"C:\capturas\b.png", history.Items[1].Path);
    }

    [Fact]
    public void Capturas_nao_salvas_nunca_se_confundem_entre_si()
    {
        var history = new CaptureHistory(new FixedSettings(new AppSettings()));

        history.Add(Solid(4, 4, 0), null);
        history.Add(Solid(4, 4, 0), null);

        Assert.Equal(2, history.Items.Count);
    }

    private CapturePipeline Pipeline()
    {
        var settings = new AppSettings { Save = new SaveSettings { Folder = _root } };
        var paths = new AppPaths(_root, _root, _root);
        return new CapturePipeline(new FixedSettings(settings), paths, Serilog.Core.Logger.None);
    }

    private static CapturedImage Solid(int width, int height, byte value)
    {
        var pixels = new byte[width * height * CapturedImage.BytesPerPixel];
        for (int index = 0; index < pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            pixels[index] = value;
            pixels[index + 1] = value;
            pixels[index + 2] = value;
            pixels[index + 3] = 255;
        }

        return new CapturedImage(pixels, new PixelRect(0, 0, width, height));
    }

    private sealed class FixedSettings(AppSettings current) : ISettingsService
    {
        public AppSettings Current { get; private set; } = current;

        public event EventHandler<AppSettings>? Changed;

        public void Load()
        {
        }

        public void Update(Func<AppSettings, AppSettings> change)
        {
            Current = change(Current);
            Changed?.Invoke(this, Current);
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
