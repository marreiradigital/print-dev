using PrintDev.Core.Capture;
using PrintDev.Core.Clipboard;
using PrintDev.Core.Configuration;
using PrintDev.Core.Screens;

namespace PrintDev.Tests.Clipboard;

public class TextDestinationTests
{
    [Theory]
    [InlineData("WindowsTerminal")]
    [InlineData("Code")]
    [InlineData("Cursor")]
    [InlineData("pwsh")]
    [InlineData("idea64")]
    [InlineData("code")]
    [InlineData("Code.exe")]
    [InlineData("Antigravity IDE")]
    [InlineData("  WINDOWSTERMINAL.EXE ")]
    public void Terminal_e_ide_recebem_o_caminho(string process)
    {
        Assert.True(TextDestination.Accepts(process, TextDestination.Defaults));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("WhatsApp")]
    [InlineData("Discord")]
    [InlineData("Slack")]
    [InlineData("WINWORD")]
    [InlineData("explorer")]
    [InlineData("TextInputHost")]
    [InlineData("svchost")]
    [InlineData("")]
    [InlineData(null)]
    public void Programa_de_imagem_e_quem_nao_se_identifica_ficam_com_a_imagem(string? process)
    {
        // O historico do Win+V e um leitor de fundo: se ele recebesse o caminho, o Win+V
        // mostraria o texto no lugar da miniatura, que e exatamente o defeito relatado.
        Assert.False(TextDestination.Accepts(process, TextDestination.Defaults));
    }

    [Fact]
    public void Nome_parecido_nao_basta()
    {
        // "Code" na lista nao pode aceitar "CodeHelper" nem "MyCode": a comparacao e do
        // nome inteiro.
        Assert.False(TextDestination.Accepts("CodeHelper", TextDestination.Defaults));
        Assert.False(TextDestination.Accepts("MyCode", TextDestination.Defaults));
    }

    [Fact]
    public void Lista_escrita_a_mao_e_lida_sem_repeticao_nem_extensao()
    {
        IReadOnlyList<string> parsed = TextDestination.Parse("Code.exe, cursor;\n  pwsh ,code,, \"Zed.exe\"\r\n");

        Assert.Equal(["Code", "cursor", "pwsh", "Zed"], parsed);
        Assert.Empty(TextDestination.Parse("  ,; \n"));
        Assert.Empty(TextDestination.Parse(null));
    }

    [Fact]
    public void Lista_padrao_nao_tem_repeticao()
    {
        Assert.Equal(
            TextDestination.Defaults.Count,
            TextDestination.Defaults.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Imagem_e_caminho_manda_o_caminho_so_para_a_lista()
    {
        ClipboardPayload payload = ForCapture(ClipboardContent.ImagemECaminho);

        Assert.NotNull(payload.Image);
        Assert.NotNull(payload.Text);
        Assert.Same(Settings.TextDestinations, payload.TextOnlyFor);
    }

    [Fact]
    public void Modo_para_todos_publica_o_caminho_para_qualquer_um()
    {
        ClipboardPayload payload = ForCapture(ClipboardContent.ImagemECaminhoParaTodos);

        Assert.NotNull(payload.Image);
        Assert.NotNull(payload.Text);
        Assert.Null(payload.TextOnlyFor);
    }

    [Fact]
    public void So_a_imagem_e_so_o_caminho_nao_dependem_de_quem_cola()
    {
        ClipboardPayload image = ForCapture(ClipboardContent.Imagem);
        Assert.NotNull(image.Image);
        Assert.Null(image.Text);
        Assert.Null(image.TextOnlyFor);

        ClipboardPayload path = ForCapture(ClipboardContent.Caminho);
        Assert.Null(path.Image);
        Assert.NotNull(path.Text);
        Assert.Null(path.TextOnlyFor);
    }

    [Fact]
    public void Sem_arquivo_em_disco_vai_so_a_imagem()
    {
        ClipboardPayload payload = ClipboardPayload.ForCapture(
            Image(), savedPath: null, png: default, Settings, ClipboardContent.ImagemECaminho);

        Assert.NotNull(payload.Image);
        Assert.Null(payload.Text);
    }

    [Fact]
    public void Configuracao_nova_vai_e_volta_do_arquivo()
    {
        AppSettings original = new AppSettings() with
        {
            Clipboard = new ClipboardSettings() with
            {
                Content = ClipboardContent.ImagemECaminhoParaTodos,
                TextDestinations = ["Code", "WindowsTerminal"],
            },
        };

        string json = SettingsSerializer.Serialize(original);
        AppSettings back = SettingsSerializer.Deserialize(json);

        Assert.Contains("\"programasQueRecebemCaminho\"", json);
        Assert.Contains("\"imagemECaminhoParaTodos\"", json);
        Assert.Equal(ClipboardContent.ImagemECaminhoParaTodos, back.Clipboard.Content);
        Assert.Equal(["Code", "WindowsTerminal"], back.Clipboard.TextDestinations);
    }

    [Fact]
    public void Arquivo_antigo_sem_a_lista_ganha_a_de_fabrica()
    {
        AppSettings back = SettingsSerializer.Deserialize("""{ "areaDeTransferencia": { "conteudo": "imagemECaminho" } }""");

        Assert.Equal(TextDestination.Defaults, back.Clipboard.TextDestinations);
    }

    private static ClipboardSettings Settings { get; } = new();

    private static ClipboardPayload ForCapture(ClipboardContent content)
        => ClipboardPayload.ForCapture(Image(), @"C:\capturas\a.png", default, Settings, content);

    private static CapturedImage Image()
        => new(new byte[2 * 2 * CapturedImage.BytesPerPixel], new PixelRect(0, 0, 2, 2));
}
