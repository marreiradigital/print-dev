using PrintDev.Core.Configuration;
using PrintDev.Core.Picker;

namespace PrintDev.Tests.Picker;

public class ColorFormatterTests
{
    [Theory]
    [InlineData(0xEC, 0x00, 0x8C, "#ec008c")]
    [InlineData(0x00, 0x00, 0x00, "#000000")]
    [InlineData(0xFF, 0xFF, 0xFF, "#ffffff")]
    [InlineData(0x0A, 0x0B, 0x0C, "#0a0b0c")]
    public void Hex_com_dois_digitos_por_canal(byte r, byte g, byte b, string esperado)
    {
        Assert.Equal(esperado, ColorFormatter.Format(r, g, b, ColorFormat.Hex));
    }

    [Fact]
    public void Hex_em_maiusculas_quando_pedido()
    {
        Assert.Equal("#EC008C", ColorFormatter.Format(0xEC, 0x00, 0x8C, ColorFormat.Hex, uppercase: true));
    }

    [Fact]
    public void Prefixo_0x_mantem_o_x_minusculo_mesmo_em_maiusculas()
    {
        // "0XEC008C" nao e um literal valido em linguagem nenhuma que use este prefixo.
        Assert.Equal("0xEC008C", ColorFormatter.Format(0xEC, 0x00, 0x8C, ColorFormat.Hex0X, uppercase: true));
    }

    [Fact]
    public void Rgb_com_separador_e_espaco()
    {
        Assert.Equal("rgb(236, 0, 140)", ColorFormatter.Format(0xEC, 0x00, 0x8C, ColorFormat.Rgb));
    }

    [Fact]
    public void Rgb_nao_e_afetado_por_maiusculas()
    {
        Assert.Equal(
            "rgb(236, 0, 140)",
            ColorFormatter.Format(0xEC, 0x00, 0x8C, ColorFormat.Rgb, uppercase: true));
    }

    [Theory]
    [InlineData(0, 0, 0, "hsl(0, 0%, 0%)")]
    [InlineData(255, 255, 255, "hsl(0, 0%, 100%)")]
    [InlineData(255, 0, 0, "hsl(0, 100%, 50%)")]
    [InlineData(0, 255, 0, "hsl(120, 100%, 50%)")]
    [InlineData(0, 0, 255, "hsl(240, 100%, 50%)")]
    public void Hsl_das_cores_de_referencia(byte r, byte g, byte b, string esperado)
    {
        Assert.Equal(esperado, ColorFormatter.Format(r, g, b, ColorFormat.Hsl));
    }

    [Fact]
    public void Hsl_do_magenta_da_marca()
    {
        // #EC008C: matiz proxima de 325 graus, saturacao total, luminosidade media.
        Assert.Equal("hsl(324, 100%, 46%)", ColorFormatter.Format(0xEC, 0x00, 0x8C, ColorFormat.Hsl));
    }

    [Fact]
    public void Hsl_usa_ponto_como_separador_independentemente_da_cultura()
    {
        // A maquina esta em pt-BR, onde a virgula e o separador decimal. Sem cultura
        // invariante, sairia "hsl(324,5, 100%, 46%)" e quebraria a folha de estilo.
        string resultado = ColorFormatter.Format(0x80, 0x40, 0xC0, ColorFormat.Hsl);

        Assert.DoesNotContain(",5", resultado);
        Assert.StartsWith("hsl(", resultado);
    }

    [Fact]
    public void Usa_as_configuracoes_quando_recebe_a_secao()
    {
        var settings = new ColorSettings { Format = ColorFormat.Rgb, UppercaseHex = true };

        Assert.Equal("rgb(1, 2, 3)", ColorFormatter.Format(1, 2, 3, settings));
    }
}
