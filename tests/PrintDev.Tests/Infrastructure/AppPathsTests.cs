using System.IO;
using PrintDev.Core.Infrastructure;

namespace PrintDev.Tests.Infrastructure;

public class AppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printdev-testes", Guid.NewGuid().ToString("N"));

    private AppPaths CreatePaths() => new(
        roamingRoot: Path.Combine(_root, "Roaming"),
        localRoot: Path.Combine(_root, "Local"),
        picturesRoot: Path.Combine(_root, "Imagens"));

    [Fact]
    public void Configuracao_e_log_ficam_em_roaming_sob_a_pasta_do_produto()
    {
        AppPaths paths = CreatePaths();

        Assert.Equal(Path.Combine(_root, "Roaming", "PrintDev"), paths.SettingsDirectory);
        Assert.Equal(Path.Combine(_root, "Roaming", "PrintDev", "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(_root, "Roaming", "PrintDev", "logs"), paths.LogsDirectory);
    }

    [Fact]
    public void Cache_fica_em_local_porque_e_conteudo_derivado_e_pesado()
    {
        AppPaths paths = CreatePaths();

        Assert.Equal(Path.Combine(_root, "Local", "PrintDev", "cache"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(_root, "Local", "PrintDev", "capturas"), paths.FallbackCapturesDirectory);
    }

    [Fact]
    public void Capturas_ficam_na_pasta_de_imagens_do_usuario()
    {
        Assert.Equal(
            Path.Combine(_root, "Imagens", "PrintDev"),
            CreatePaths().DefaultCapturesDirectory);
    }

    [Fact]
    public void EnsureCreated_cria_configuracao_log_e_cache()
    {
        AppPaths paths = CreatePaths();

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.SettingsDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.CacheDirectory));
    }

    [Fact]
    public void EnsureCreated_NAO_cria_a_pasta_de_capturas()
    {
        // Quem instala, abre e desiste nao deve ganhar uma pasta vazia em Imagens.
        // A pasta nasce na primeira captura, nao na primeira execucao.
        AppPaths paths = CreatePaths();

        paths.EnsureCreated();

        Assert.False(Directory.Exists(paths.DefaultCapturesDirectory));
    }

    [Fact]
    public void EnsureCreated_pode_ser_chamado_duas_vezes()
    {
        AppPaths paths = CreatePaths();

        paths.EnsureCreated();
        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.SettingsDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Raiz_vazia_e_recusada(string invalidRoot)
    {
        Assert.Throws<ArgumentException>(() => new AppPaths(invalidRoot, invalidRoot, invalidRoot));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
