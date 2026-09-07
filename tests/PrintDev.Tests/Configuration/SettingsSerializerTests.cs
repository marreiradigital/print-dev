using System.Text.Json;
using PrintDev.Core.Configuration;

namespace PrintDev.Tests.Configuration;

public class SettingsSerializerTests
{
    [Fact]
    public void Ida_e_volta_preserva_tudo()
    {
        var original = new AppSettings
        {
            Save = new SaveSettings { Format = ImageFormat.Jpeg, JpegQuality = 70, Folder = @"D:\Prints" },
            Clipboard = new ClipboardSettings { PathFormat = PathTextFormat.Wsl, IncludeFileDrop = true },
        };

        AppSettings volta = SettingsSerializer.Deserialize(SettingsSerializer.Serialize(original));

        Assert.Equal(original, volta);
    }

    [Fact]
    public void Enum_e_gravado_como_texto_em_camelCase()
    {
        string json = SettingsSerializer.Serialize(new AppSettings
        {
            Save = new SaveSettings { Format = ImageFormat.Jpeg, DateSubfolder = DateSubfolder.AnoMes },
        });

        // Numero seria fragil: reordenar os membros do enum mudaria o significado do
        // arquivo ja gravado no disco do usuario.
        Assert.Contains("\"formato\": \"jpeg\"", json);
        Assert.Contains("\"subpastaPorData\": \"anoMes\"", json);
    }

    [Fact]
    public void Chaves_do_json_sao_em_portugues()
    {
        string json = SettingsSerializer.Serialize(new AppSettings());

        Assert.Contains("\"areaDeTransferencia\"", json);
        Assert.Contains("\"salvamento\"", json);
        Assert.Contains("\"versaoDoSchema\"", json);
        Assert.DoesNotContain("\"Clipboard\"", json);
    }

    [Fact]
    public void Chave_desconhecida_na_raiz_sobrevive_a_regravacao()
    {
        // Cenario real: uma versao mais nova gravou uma secao que este codigo nao
        // conhece. Reler e regravar nao pode apagar a configuracao dela.
        const string json = """
            { "versaoDoSchema": 1, "secaoDoFuturo": { "algo": 42 } }
            """;

        AppSettings lido = SettingsSerializer.Deserialize(json);
        string regravado = SettingsSerializer.Serialize(lido);

        Assert.Contains("secaoDoFuturo", regravado);
        Assert.Contains("42", regravado);
    }

    [Fact]
    public void Aceita_comentario_e_virgula_sobrando_de_quem_editou_a_mao()
    {
        const string json = """
            {
              // o usuario prefere JPEG
              "salvamento": { "formato": "jpeg", "qualidadeJpeg": 80, },
            }
            """;

        AppSettings lido = SettingsSerializer.Deserialize(json);

        Assert.Equal(ImageFormat.Jpeg, lido.Save.Format);
        Assert.Equal(80, lido.Save.JpegQuality);
    }

    [Fact]
    public void Secao_ausente_cai_nos_padroes()
    {
        AppSettings lido = SettingsSerializer.Deserialize("""{ "versaoDoSchema": 1 }""");

        Assert.Equal(ImageFormat.Png, lido.Save.Format);
        Assert.Equal(ClipboardContent.ImagemECaminho, lido.Clipboard.Content);
        Assert.True(lido.Clipboard.CopyAutomatically);
    }

    [Fact]
    public void Json_invalido_estoura_para_quem_chama_decidir()
    {
        Assert.Throws<JsonException>(() => SettingsSerializer.Deserialize("{ isto nao e json"));
    }

    [Fact]
    public void Padrao_de_fabrica_confere_com_o_combinado()
    {
        var padrao = new AppSettings();

        Assert.Equal(ImageFormat.Png, padrao.Save.Format);
        Assert.Equal(ClipboardContent.ImagemECaminho, padrao.Clipboard.Content);
        Assert.Equal(PathTextFormat.Windows, padrao.Clipboard.PathFormat);
        Assert.Equal(QuoteMode.QuandoTiverEspaco, padrao.Clipboard.Quotes);

        // Os dois formatos que causam efeito colateral em app de terceiro nascem
        // desligados. Ver os comentarios em ClipboardSettings.
        Assert.False(padrao.Clipboard.IncludeFileDrop);
        Assert.False(padrao.Clipboard.IncludeHtml);

        // Apagar arquivo do usuario nunca comeca ligado.
        Assert.False(padrao.Cleanup.Enabled);
        Assert.True(padrao.Cleanup.OnlyPrintDevFiles);
        Assert.True(padrao.Cleanup.MoveToRecycleBin);

        // Encerrar processo alheio exige confirmacao.
        Assert.Equal(CompetitorPolicy.Perguntar, padrao.Advanced.OnCompetitorDetected);
    }
}
