using PrintDev.Core.Configuration;
using PrintDev.Core.Paths;

namespace PrintDev.Tests.Paths;

public class PathTextFormatterTests
{
    private const string ComEspaco = @"C:\Users\conta\Pictures\PrintDev\meu print.png";
    private const string SemEspaco = @"C:\Users\conta\Pictures\PrintDev\print.png";

    [Fact]
    public void Windows_com_espaco_ganha_aspas()
    {
        // E o caso que decide se colar num terminal funciona sem o usuario consertar.
        Assert.Equal(
            "\"" + ComEspaco + "\"",
            PathTextFormatter.Format(ComEspaco, PathTextFormat.Windows));
    }

    [Fact]
    public void Windows_sem_espaco_nao_ganha_aspas()
    {
        Assert.Equal(SemEspaco, PathTextFormatter.Format(SemEspaco, PathTextFormat.Windows));
    }

    [Theory]
    [InlineData(QuoteMode.Nunca, false)]
    [InlineData(QuoteMode.Sempre, true)]
    [InlineData(QuoteMode.QuandoTiverEspaco, false)]
    public void Politica_de_aspas_em_caminho_sem_espaco(QuoteMode modo, bool esperaAspas)
    {
        string resultado = PathTextFormatter.Format(SemEspaco, PathTextFormat.Windows, modo);

        Assert.Equal(esperaAspas, resultado.StartsWith('"'));
    }

    [Fact]
    public void Aspas_nao_sao_duplicadas()
    {
        string uma = PathTextFormatter.Format(ComEspaco, PathTextFormat.Windows, QuoteMode.Sempre);

        Assert.Equal(2, uma.Count(c => c == '"'));
    }

    [Fact]
    public void Barra_normal_troca_o_separador()
    {
        Assert.Equal(
            "C:/Users/conta/Pictures/PrintDev/print.png",
            PathTextFormatter.Format(SemEspaco, PathTextFormat.BarraNormal));
    }

    [Fact]
    public void Wsl_traduz_a_unidade_para_ponto_de_montagem()
    {
        Assert.Equal(
            "/mnt/c/Users/conta/Pictures/PrintDev/print.png",
            PathTextFormatter.Format(SemEspaco, PathTextFormat.Wsl));
    }

    [Fact]
    public void Wsl_com_raiz_do_git_bash()
    {
        // O Git Bash monta as unidades direto na raiz: /c/... em vez de /mnt/c/...
        Assert.Equal(
            "/c/Users/conta/Pictures/PrintDev/print.png",
            PathTextFormatter.Format(SemEspaco, PathTextFormat.Wsl, QuoteMode.Nunca, wslRoot: "/"));
    }

    [Fact]
    public void Wsl_normaliza_a_letra_da_unidade_para_minuscula()
    {
        Assert.StartsWith("/mnt/d/", PathTextFormatter.ToWsl(@"D:\Prints\a.png"));
    }

    [Fact]
    public void Wsl_de_caminho_de_rede_devolve_o_formato_do_windows()
    {
        // No WSL um compartilhamento de rede e alcancado por ponto de montagem que so a
        // maquina de destino conhece. Inventar uma traducao daria um caminho que nao
        // existe em lugar nenhum.
        const string rede = @"\\servidor\compartilhado\print.png";

        Assert.Equal(rede, PathTextFormatter.ToWsl(rede));
    }

    [Fact]
    public void Wsl_com_espaco_ganha_aspas()
    {
        Assert.Equal(
            "\"/mnt/c/Users/conta/Pictures/PrintDev/meu print.png\"",
            PathTextFormatter.Format(ComEspaco, PathTextFormat.Wsl));
    }

    [Fact]
    public void Endereco_de_arquivo_codifica_o_espaco()
    {
        Assert.Equal(
            "file:///C:/Users/conta/Pictures/PrintDev/meu%20print.png",
            PathTextFormatter.Format(ComEspaco, PathTextFormat.UriFile));
    }

    [Fact]
    public void Endereco_de_arquivo_nunca_ganha_aspas()
    {
        // Aspas quebrariam o endereco, entao a politica de aspas nao se aplica a ele.
        string resultado = PathTextFormatter.Format(ComEspaco, PathTextFormat.UriFile, QuoteMode.Sempre);

        Assert.DoesNotContain("\"", resultado);
    }

    [Fact]
    public void Markdown_usa_o_nome_sem_extensao_como_texto_alternativo()
    {
        Assert.Equal(
            "![meu print](file:///C:/Users/conta/Pictures/PrintDev/meu%20print.png)",
            PathTextFormatter.Format(ComEspaco, PathTextFormat.Markdown));
    }

    [Fact]
    public void Markdown_nunca_ganha_aspas_em_volta()
    {
        string resultado = PathTextFormatter.Format(ComEspaco, PathTextFormat.Markdown, QuoteMode.Sempre);

        Assert.StartsWith("![", resultado);
    }

    [Fact]
    public void Html_monta_a_marcacao_com_aspas_de_verdade()
    {
        // O marcador {aspa} existe para o modelo poder produzir aspas sem que o arquivo
        // JSON precise escapa-las, o que o tornaria ilegivel para quem edita a mao.
        Assert.Equal(
            "<img src=\"file:///C:/Users/conta/Pictures/PrintDev/print.png\" alt=\"print\">",
            PathTextFormatter.Format(SemEspaco, PathTextFormat.Html));
    }

    [Fact]
    public void Somente_nome_devolve_so_o_arquivo()
    {
        Assert.Equal("print.png", PathTextFormatter.Format(SemEspaco, PathTextFormat.SomenteNome));
        Assert.Equal("\"meu print.png\"", PathTextFormatter.Format(ComEspaco, PathTextFormat.SomenteNome));
    }

    [Fact]
    public void Modelo_personalizado_e_respeitado()
    {
        Assert.Equal(
            "print -> file:///C:/Users/conta/Pictures/PrintDev/print.png",
            PathTextFormatter.Format(
                SemEspaco,
                PathTextFormat.Markdown,
                markdownTemplate: "{nome} -> {caminho}"));
    }

    [Fact]
    public void Usa_as_configuracoes_quando_recebe_a_secao_inteira()
    {
        var settings = new ClipboardSettings { PathFormat = PathTextFormat.Wsl, Quotes = QuoteMode.Nunca };

        Assert.Equal(
            "/mnt/c/Users/conta/Pictures/PrintDev/meu print.png",
            PathTextFormatter.Format(ComEspaco, settings));
    }

    [Fact]
    public void Espaco_em_volta_do_caminho_e_aparado_antes_de_tudo()
    {
        Assert.Equal(SemEspaco, PathTextFormatter.Format("   " + SemEspaco + "   ", PathTextFormat.Windows));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Caminho_vazio_e_recusado(string caminho)
    {
        Assert.Throws<ArgumentException>(() => PathTextFormatter.Format(caminho, PathTextFormat.Windows));
    }
}
