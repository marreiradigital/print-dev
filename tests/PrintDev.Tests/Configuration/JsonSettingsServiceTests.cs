using System.IO;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;

namespace PrintDev.Tests.Configuration;

public class JsonSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printdev-testes", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public JsonSettingsServiceTests()
    {
        _paths = new AppPaths(
            roamingRoot: Path.Combine(_root, "Roaming"),
            localRoot: Path.Combine(_root, "Local"),
            picturesRoot: Path.Combine(_root, "Imagens"));

        _paths.EnsureCreated();
    }

    private JsonSettingsService CreateService()
        => new(_paths, Serilog.Core.Logger.None);

    [Fact]
    public void Primeira_execucao_cria_o_arquivo_com_os_padroes()
    {
        using JsonSettingsService service = CreateService();

        service.Load();

        Assert.True(File.Exists(_paths.SettingsFile));
        Assert.Equal(new AppSettings(), service.Current);
    }

    [Fact]
    public void Le_o_que_ja_estava_no_disco()
    {
        File.WriteAllText(_paths.SettingsFile, """
            { "versaoDoSchema": 1, "salvamento": { "formato": "jpeg", "qualidadeJpeg": 55 } }
            """);

        using JsonSettingsService service = CreateService();
        service.Load();

        Assert.Equal(ImageFormat.Jpeg, service.Current.Save.Format);
        Assert.Equal(55, service.Current.Save.JpegQuality);
    }

    [Fact]
    public void Arquivo_corrompido_e_posto_de_lado_e_o_programa_sobe_com_os_padroes()
    {
        File.WriteAllText(_paths.SettingsFile, "{ isto nao e json valido");

        using JsonSettingsService service = CreateService();
        service.Load();

        Assert.Equal(new AppSettings(), service.Current);

        // O arquivo do usuario nao pode simplesmente sumir: pode ter horas de ajuste.
        string[] guardados = Directory.GetFiles(_paths.SettingsDirectory, "settings.corrompido-*.json");
        Assert.Single(guardados);
        Assert.Contains("isto nao e json valido", File.ReadAllText(guardados[0]));
    }

    [Fact]
    public void Update_troca_o_valor_na_hora_e_avisa()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        AppSettings? avisado = null;
        service.Changed += (_, s) => avisado = s;

        service.Update(s => s with { Save = s.Save with { JpegQuality = 42 } });

        Assert.Equal(42, service.Current.Save.JpegQuality);
        Assert.NotNull(avisado);
        Assert.Equal(42, avisado.Save.JpegQuality);
    }

    [Fact]
    public void Update_que_nao_muda_nada_nao_avisa_nem_grava()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        bool avisou = false;
        service.Changed += (_, _) => avisou = true;

        service.Update(s => s with { Save = s.Save with { JpegQuality = s.Save.JpegQuality } });

        Assert.False(avisou);
    }

    [Fact]
    public void Flush_grava_o_que_estava_pendente()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        service.Update(s => s with { Save = s.Save with { Format = ImageFormat.Jpeg } });
        service.Flush();

        Assert.Contains("\"formato\": \"jpeg\"", File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void Dispose_grava_o_ajuste_que_ainda_nao_tinha_ido_para_o_disco()
    {
        // Encerrar o programa logo apos mexer numa configuracao nao pode perder o ajuste,
        // porque a gravacao e adiada de proposito.
        using (JsonSettingsService service = CreateService())
        {
            service.Load();
            service.Update(s => s with { History = s.History with { Count = 99 } });
        }

        Assert.Contains("\"quantidade\": 99", File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void Gravacao_deixa_copia_de_seguranca_da_versao_anterior()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        service.Update(s => s with { History = s.History with { Count = 5 } });
        service.Flush();

        Assert.True(File.Exists(_paths.SettingsFile + ".bak"));
        // O temporario nunca pode sobrar: seria lixo permanente na pasta do usuario.
        Assert.False(File.Exists(_paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void Edicao_do_arquivo_por_fora_volta_para_o_programa()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        using var recarregou = new ManualResetEventSlim(false);
        service.Changed += (_, _) => recarregou.Set();

        File.WriteAllText(_paths.SettingsFile, """
            { "versaoDoSchema": 1, "historico": { "quantidade": 7 } }
            """);

        Assert.True(recarregou.Wait(TimeSpan.FromSeconds(10)), "O observador do arquivo não avisou a tempo.");
        Assert.Equal(7, service.Current.History.Count);
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
