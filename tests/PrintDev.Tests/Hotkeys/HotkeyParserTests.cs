using PrintDev.Core.Hotkeys;

namespace PrintDev.Tests.Hotkeys;

public class HotkeyParserTests
{
    private const uint VkPrintScreen = 0x2C;
    private const uint VkS = 0x53;

    [Fact]
    public void PrtSc_sozinho()
    {
        Assert.True(HotkeyParser.TryParse("PrtSc", out HotkeyDefinition hotkey, out string? erro));

        Assert.Null(erro);
        Assert.Equal(HotkeyModifiers.None, hotkey.Modifiers);
        Assert.Equal(VkPrintScreen, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("PrtSc")]
    [InlineData("PrintScreen")]
    [InlineData("printscreen")]
    [InlineData("Print")]
    [InlineData("Snapshot")]
    [InlineData("  prtsc  ")]
    public void Aceita_as_grafias_usuais_da_tecla_print_screen(string texto)
    {
        Assert.True(HotkeyParser.TryParse(texto, out HotkeyDefinition hotkey, out _));
        Assert.Equal(VkPrintScreen, hotkey.VirtualKey);
    }

    [Fact]
    public void Modificadores_combinam()
    {
        Assert.True(HotkeyParser.TryParse("Ctrl+Shift+PrtSc", out HotkeyDefinition hotkey, out _));

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, hotkey.Modifiers);
        Assert.Equal(VkPrintScreen, hotkey.VirtualKey);
    }

    [Theory]
    [InlineData("ctrl+alt+p")]
    [InlineData("CONTROL+ALT+P")]
    [InlineData("Controle + Alt + P")]
    public void Nome_do_modificador_nao_diferencia_maiusculas_nem_espaco(string texto)
    {
        Assert.True(HotkeyParser.TryParse(texto, out HotkeyDefinition hotkey, out _));

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, hotkey.Modifiers);
        Assert.Equal('P', (char)hotkey.VirtualKey);
    }

    [Fact]
    public void Ida_e_volta_devolve_a_forma_canonica()
    {
        // A ordem de saida e sempre Ctrl, Alt, Shift, Win - independente de como foi
        // escrito. E o que faz o arquivo de configuracoes parar de mudar sozinho.
        Assert.True(HotkeyParser.TryParse("Shift+Win+Alt+Ctrl+F5", out HotkeyDefinition hotkey, out _));

        Assert.Equal("Ctrl+Alt+Shift+Win+F5", HotkeyParser.Format(hotkey));
    }

    [Theory]
    [InlineData("Ctrl+PrtSc")]
    [InlineData("Alt+F4")]
    [InlineData("Ctrl+Shift+V")]
    [InlineData("Alt+Win+Espaco")]
    public void Formatar_o_que_foi_lido_devolve_o_mesmo_texto(string texto)
    {
        Assert.True(HotkeyParser.TryParse(texto, out HotkeyDefinition hotkey, out _));
        Assert.Equal(texto, HotkeyParser.Format(hotkey));
    }

    [Fact]
    public void Vazio_significa_sem_atalho_e_nao_e_erro()
    {
        Assert.True(HotkeyParser.TryParse("", out HotkeyDefinition hotkey, out string? erro));

        Assert.Null(erro);
        Assert.False(hotkey.IsSet);
        Assert.Equal(string.Empty, HotkeyParser.Format(hotkey));
    }

    [Fact]
    public void So_modificador_nao_e_atalho()
    {
        Assert.False(HotkeyParser.TryParse("Ctrl+Shift", out _, out string? erro));
        Assert.Contains("tecla principal", erro);
    }

    [Fact]
    public void Duas_teclas_principais_e_recusado()
    {
        Assert.False(HotkeyParser.TryParse("Ctrl+A+B", out _, out string? erro));
        Assert.Contains("mais de uma tecla", erro);
    }

    [Fact]
    public void Tecla_inexistente_e_recusada_com_o_nome_no_erro()
    {
        Assert.False(HotkeyParser.TryParse("Ctrl+Banana", out _, out string? erro));
        Assert.Contains("Banana", erro);
    }

    [Fact]
    public void Combinacao_reservada_pelo_windows_e_recusada_com_motivo()
    {
        // O Windows nunca entrega Win+Shift+S a aplicacao nenhuma. Recusar aqui, com
        // explicacao, evita o usuario configurar algo que nunca vai funcionar.
        Assert.False(HotkeyParser.TryParse("Win+Shift+S", out _, out string? erro));
        Assert.Contains("Ferramenta de Captura", erro);
    }

    [Theory]
    [InlineData("Win+L")]
    [InlineData("Ctrl+Alt+Delete")]
    [InlineData("Win+Tab")]
    public void As_demais_combinacoes_do_sistema_tambem_sao_recusadas(string texto)
    {
        Assert.False(HotkeyParser.TryParse(texto, out _, out string? erro));
        Assert.False(string.IsNullOrWhiteSpace(erro));
    }

    [Fact]
    public void Shift_mais_S_sozinho_continua_valido()
    {
        // A reserva e da COMBINACAO, nao da tecla: recusar o S inteiro por causa do
        // Win+Shift+S seria tirar do usuario uma tecla perfeitamente utilizavel.
        Assert.True(HotkeyParser.TryParse("Ctrl+Shift+S", out HotkeyDefinition hotkey, out _));
        Assert.Equal(VkS, hotkey.VirtualKey);
    }

    [Fact]
    public void NoRepeat_e_sempre_acrescentado_no_registro()
    {
        // Sem NoRepeat, segurar a tecla abriria um seletor atras do outro.
        HotkeyParser.TryParse("PrtSc", out HotkeyDefinition hotkey, out _);

        Assert.Equal((uint)HotkeyModifiers.NoRepeat, hotkey.ModifiersForRegistration);
        Assert.False(hotkey.Modifiers.HasFlag(HotkeyModifiers.NoRepeat));
    }

    [Theory]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x41, "A")]
    [InlineData(0x39, "9")]
    [InlineData(0x2C, "PrtSc")]
    public void Nome_da_tecla_para_exibicao(uint tecla, string esperado)
    {
        Assert.Equal(esperado, HotkeyParser.NameOf(tecla));
    }
}
