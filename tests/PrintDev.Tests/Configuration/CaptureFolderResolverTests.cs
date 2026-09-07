using System.IO;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;

namespace PrintDev.Tests.Configuration;

public class CaptureFolderResolverTests
{
    private static readonly AppPaths Paths = new(
        roamingRoot: Path.Combine("R:", "Roaming"),
        localRoot: Path.Combine("R:", "Local"),
        picturesRoot: Path.Combine("R:", "Imagens"));

    private static readonly DateTime Quando = new(2026, 9, 7, 14, 32, 10);

    [Fact]
    public void Sem_pasta_configurada_usa_a_padrao_em_imagens()
    {
        Assert.Equal(
            Path.Combine("R:", "Imagens", "PrintDev"),
            CaptureFolderResolver.ResolveRoot(new AppSettings(), Paths));
    }

    [Fact]
    public void Pasta_configurada_ganha_da_padrao()
    {
        var settings = new AppSettings { Save = new SaveSettings { Folder = @"D:\Prints" } };

        Assert.Equal(@"D:\Prints", CaptureFolderResolver.ResolveRoot(settings, Paths));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Pasta_em_branco_conta_como_nao_configurada(string configurada)
    {
        var settings = new AppSettings { Save = new SaveSettings { Folder = configurada } };

        Assert.Equal(Paths.DefaultCapturesDirectory, CaptureFolderResolver.ResolveRoot(settings, Paths));
    }

    [Fact]
    public void Espaco_em_volta_do_caminho_e_aparado()
    {
        var settings = new AppSettings { Save = new SaveSettings { Folder = @"  D:\Prints  " } };

        Assert.Equal(@"D:\Prints", CaptureFolderResolver.ResolveRoot(settings, Paths));
    }

    [Theory]
    [InlineData(DateSubfolder.Nenhuma, "")]
    [InlineData(DateSubfolder.Ano, "2026")]
    public void Subpasta_por_data_sem_separador(DateSubfolder modo, string esperado)
    {
        Assert.Equal(esperado, CaptureFolderResolver.DateSegment(modo, Quando));
    }

    [Fact]
    public void Subpasta_por_ano_e_mes()
    {
        Assert.Equal(Path.Combine("2026", "09"), CaptureFolderResolver.DateSegment(DateSubfolder.AnoMes, Quando));
    }

    [Fact]
    public void Subpasta_por_ano_mes_e_dia()
    {
        Assert.Equal(
            Path.Combine("2026", "09", "07"),
            CaptureFolderResolver.DateSegment(DateSubfolder.AnoMesDia, Quando));
    }

    [Fact]
    public void Resolve_junta_a_raiz_com_a_subpasta()
    {
        var settings = new AppSettings
        {
            Save = new SaveSettings { Folder = @"D:\Prints", DateSubfolder = DateSubfolder.AnoMes },
        };

        Assert.Equal(
            Path.Combine(@"D:\Prints", "2026", "09"),
            CaptureFolderResolver.Resolve(settings, Paths, Quando));
    }

    [Fact]
    public void Sem_subpasta_o_resultado_e_a_propria_raiz()
    {
        var settings = new AppSettings { Save = new SaveSettings { Folder = @"D:\Prints" } };

        Assert.Equal(@"D:\Prints", CaptureFolderResolver.Resolve(settings, Paths, Quando).TrimEnd(Path.DirectorySeparatorChar));
    }
}
