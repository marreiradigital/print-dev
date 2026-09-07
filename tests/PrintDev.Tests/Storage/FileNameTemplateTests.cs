using PrintDev.Core.Storage;

namespace PrintDev.Tests.Storage;

public class FileNameTemplateTests
{
    private static readonly FileNameContext Contexto = new(
        When: new DateTime(2026, 9, 7, 14, 32, 10, 456),
        AppName: "chrome",
        WindowTitle: "Painel do cliente - Google Chrome",
        MonitorName: "DISPLAY2",
        Width: 1280,
        Height: 720,
        Mode: "regiao",
        Counter: 3);

    [Fact]
    public void Modelo_padrao()
    {
        Assert.Equal(
            "PrintDev_2026-09-07_143210",
            FileNameTemplate.Render("PrintDev_{ano}-{mes}-{dia}_{hora}{min}{seg}", Contexto));
    }

    [Theory]
    [InlineData("{ano}", "2026")]
    [InlineData("{mes}", "09")]
    [InlineData("{dia}", "07")]
    [InlineData("{hora}", "14")]
    [InlineData("{min}", "32")]
    [InlineData("{seg}", "10")]
    [InlineData("{ms}", "456")]
    [InlineData("{contador}", "3")]
    [InlineData("{largura}", "1280")]
    [InlineData("{altura}", "720")]
    [InlineData("{modo}", "regiao")]
    [InlineData("{app}", "chrome")]
    [InlineData("{monitor}", "DISPLAY2")]
    public void Cada_marcador_isolado(string modelo, string esperado)
    {
        Assert.Equal(esperado, FileNameTemplate.Render(modelo, Contexto));
    }

    [Fact]
    public void Mes_e_dia_vem_com_dois_digitos()
    {
        var janeiro = Contexto with { When = new DateTime(2026, 1, 5, 9, 8, 7) };

        Assert.Equal("2026-01-05_090807", FileNameTemplate.Render("{ano}-{mes}-{dia}_{hora}{min}{seg}", janeiro));
    }

    [Fact]
    public void Titulo_da_janela_tem_os_caracteres_proibidos_trocados()
    {
        var comBarra = Contexto with { WindowTitle = @"relatorio: 10/2026 <rascunho>" };

        string nome = FileNameTemplate.Render("{titulo}", comBarra);

        Assert.DoesNotContain("/", nome);
        Assert.DoesNotContain("<", nome);
        Assert.DoesNotContain(":", nome);
        Assert.Contains("relatorio", nome);
    }

    [Fact]
    public void Marcador_desconhecido_volta_como_estava_escrito()
    {
        // Sumir com ele em silencio faria o usuario achar que o modelo funcionou.
        Assert.Equal("x{banana}y", FileNameTemplate.Render("x{banana}y", Contexto));
    }

    [Fact]
    public void Chave_sem_fechamento_nao_estoura()
    {
        // Erro de digitacao no modelo nao pode custar a captura do usuario.
        Assert.Equal("PrintDev_{ano", FileNameTemplate.Render("PrintDev_{ano", Contexto));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Modelo_vazio_cai_no_padrao(string? modelo)
    {
        Assert.Equal("PrintDev_2026-09-07_143210", FileNameTemplate.Render(modelo, Contexto));
    }

    [Fact]
    public void Modelo_que_nao_sobra_nada_cai_no_padrao()
    {
        // So pontos: o Windows apaga ponto no fim do nome, entao sobra string vazia -
        // e arquivo sem nome nao existe.
        Assert.Equal("PrintDev_2026-09-07_143210", FileNameTemplate.Render("...", Contexto));
    }

    [Fact]
    public void Caractere_proibido_vira_sublinhado_em_vez_de_sumir()
    {
        // Trocar em vez de remover preserva a estrutura que o usuario escreveu: o nome
        // continua reconhecivel, e nao vira uma sopa de letras coladas.
        Assert.Equal("___", FileNameTemplate.Render("///", Contexto));
        Assert.Equal("a_b_c", FileNameTemplate.Sanitize("a/b:c"));
    }

    [Fact]
    public void Nome_muito_longo_e_cortado()
    {
        var titulao = Contexto with { WindowTitle = new string('a', 400) };

        string nome = FileNameTemplate.Render("{titulo}", titulao);

        Assert.Equal(120, nome.Length);
    }

    [Fact]
    public void Ponto_e_espaco_no_fim_sao_removidos()
    {
        // O Windows os remove sozinho e em silencio, o que faria o caminho devolvido
        // nao bater com o arquivo que existe no disco.
        Assert.Equal("captura", FileNameTemplate.Sanitize("captura.  "));
        Assert.Equal("captura", FileNameTemplate.Sanitize("  captura  "));
    }

    [Fact]
    public void Espacos_repetidos_viram_um_so()
    {
        Assert.Equal("uma captura minha", FileNameTemplate.Sanitize("uma    captura   minha"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("LPT1")]
    [InlineData("com9")]
    public void Nomes_reservados_do_windows_ganham_sufixo(string reservado)
    {
        // Um arquivo chamado CON.png nao pode ser criado, e o erro do sistema nao ajuda.
        Assert.Equal(reservado + "_", FileNameTemplate.Sanitize(reservado));
    }

    [Fact]
    public void Nome_parecido_com_reservado_nao_e_alterado()
    {
        Assert.Equal("CONTA", FileNameTemplate.Sanitize("CONTA"));
        Assert.Equal("console", FileNameTemplate.Sanitize("console"));
    }

    [Fact]
    public void Texto_literal_em_volta_dos_marcadores_e_preservado()
    {
        Assert.Equal(
            "print-2026 de setembro-fim",
            FileNameTemplate.Render("print-{ano} de setembro-fim", Contexto));
    }
}
