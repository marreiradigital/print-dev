using System.Text.RegularExpressions;
using PrintDev.Core.Storage;

namespace PrintDev.Tests.Storage;

/// <summary>
/// Estes testes são a trava de segurança da limpeza automática. Um falso positivo aqui
/// significa apagar arquivo que o usuário criou.
/// </summary>
public class FileNamePatternTests
{
    private static readonly Regex Padrao =
        FileNameTemplate.BuildPattern("PrintDev_{ano}-{mes}-{dia}_{hora}{min}{seg}");

    [Theory]
    [InlineData("PrintDev_2026-09-07_143210.png")]
    [InlineData("PrintDev_2026-09-07_143210.jpg")]
    [InlineData("PrintDev_2026-09-07_143210.jpeg")]
    [InlineData("PrintDev_2026-01-01_000000.png")]
    public void Reconhece_os_arquivos_que_o_proprio_programa_gera(string nome)
    {
        Assert.Matches(Padrao, nome);
    }

    [Theory]
    [InlineData("PrintDev_2026-09-07_143210_2.png")]
    [InlineData("PrintDev_2026-09-07_143210_15.png")]
    public void Reconhece_o_sufixo_de_desempate(string nome)
    {
        // O proprio programa gera esses nomes quando dois arquivos colidem; nao
        // reconhece-los deixaria lixo acumulando para sempre.
        Assert.Matches(Padrao, nome);
    }

    [Theory]
    [InlineData("relatorio final.png")]
    [InlineData("IMG_20260907.jpg")]
    [InlineData("Captura de tela 2026-09-07.png")]
    [InlineData("PrintDev.png")]
    [InlineData("PrintDev_2026-09-07.png")]
    [InlineData("printdev_2026-09-07_143210.png.bak")]
    [InlineData("contrato.pdf")]
    [InlineData("nota fiscal 2026-09-07_143210.png")]
    public void NAO_reconhece_arquivo_que_nao_e_nosso(string nome)
    {
        // Cada um destes e um arquivo que o usuario pode ter na pasta. Reconhecer
        // qualquer um seria apagar coisa dele.
        Assert.DoesNotMatch(Padrao, nome);
    }

    [Fact]
    public void Marcador_de_texto_livre_nao_atravessa_pasta()
    {
        Regex comTitulo = FileNameTemplate.BuildPattern("{titulo}_{ano}");

        Assert.Matches(comTitulo, "qualquer coisa aqui_2026.png");

        // Se o marcador casasse com barra, um nome forjado poderia fazer a expressao
        // aceitar caminho de outra pasta.
        Assert.DoesNotMatch(comTitulo, @"..\outra\coisa_2026.png");
        Assert.DoesNotMatch(comTitulo, "sub/pasta_2026.png");
    }

    [Fact]
    public void Extensao_diferente_de_imagem_nunca_casa()
    {
        Assert.DoesNotMatch(Padrao, "PrintDev_2026-09-07_143210.txt");
        Assert.DoesNotMatch(Padrao, "PrintDev_2026-09-07_143210.exe");
        Assert.DoesNotMatch(Padrao, "PrintDev_2026-09-07_143210");
    }

    [Fact]
    public void Modelo_com_contador_aceita_qualquer_numero()
    {
        Regex comContador = FileNameTemplate.BuildPattern("captura-{contador}");

        Assert.Matches(comContador, "captura-1.png");
        Assert.Matches(comContador, "captura-9999.png");
        Assert.DoesNotMatch(comContador, "captura-abc.png");
    }

    [Fact]
    public void Modelo_vazio_cai_no_padrao_de_fabrica()
    {
        Regex vazio = FileNameTemplate.BuildPattern(null);

        Assert.Matches(vazio, "PrintDev_2026-09-07_143210.png");
    }

    [Fact]
    public void Maiusculas_nao_impedem_o_reconhecimento()
    {
        // O sistema de arquivos do Windows nao diferencia, entao a expressao tambem nao
        // pode diferenciar - senao um arquivo nosso escaparia da limpeza.
        Assert.Matches(Padrao, "PRINTDEV_2026-09-07_143210.PNG");
    }
}
