using PrintDev.Core.Screens;

namespace PrintDev.Tests.Screens;

public class PixelRectTests
{
    [Fact]
    public void Bordas_direita_e_inferior_sao_exclusivas()
    {
        var rect = new PixelRect(10, 20, 100, 50);

        Assert.Equal(110, rect.Right);
        Assert.Equal(70, rect.Bottom);
        Assert.True(rect.Contains(10, 20));
        Assert.False(rect.Contains(110, 70));
        Assert.True(rect.Contains(109, 69));
    }

    [Theory]
    [InlineData(100, 100, 300, 400)]
    [InlineData(300, 400, 100, 100)]
    [InlineData(300, 100, 100, 400)]
    [InlineData(100, 400, 300, 100)]
    public void Arrastar_em_qualquer_direcao_produz_o_mesmo_retangulo(int x1, int y1, int x2, int y2)
    {
        // Arrastar da direita para a esquerda, ou de baixo para cima, e o jeito normal
        // de selecionar para muita gente. Largura negativa nao pode existir.
        PixelRect rect = PixelRect.FromPoints(x1, y1, x2, y2);

        Assert.Equal(new PixelRect(100, 100, 200, 300), rect);
    }

    [Fact]
    public void Area_vazia_e_reconhecida()
    {
        Assert.True(new PixelRect(0, 0, 0, 10).IsEmpty);
        Assert.True(new PixelRect(0, 0, 10, 0).IsEmpty);
        Assert.True(new PixelRect(0, 0, -5, 10).IsEmpty);
        Assert.False(new PixelRect(0, 0, 1, 1).IsEmpty);
    }

    [Fact]
    public void Intersecao_de_quem_nao_se_toca_e_vazia()
    {
        var esquerda = new PixelRect(0, 0, 100, 100);
        var direita = new PixelRect(200, 0, 100, 100);

        Assert.True(esquerda.Intersect(direita).IsEmpty);
        Assert.False(esquerda.IntersectsWith(direita));
    }

    [Fact]
    public void Retangulos_encostados_nao_se_intersectam()
    {
        // Bordas exclusivas: um termina em 100, o outro comeca em 100. Zero pixel comum.
        var esquerda = new PixelRect(0, 0, 100, 100);
        var direita = new PixelRect(100, 0, 100, 100);

        Assert.False(esquerda.IntersectsWith(direita));
    }

    [Fact]
    public void Intersecao_devolve_a_parte_comum()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, 50, 100, 100);

        Assert.Equal(new PixelRect(50, 50, 50, 50), a.Intersect(b));
    }

    [Fact]
    public void Uniao_ignora_o_vazio()
    {
        // Somar retangulos numa sequencia comecando do vazio nao pode arrastar a origem
        // para o canto zero - e exatamente como o retangulo dos monitores e calculado.
        var monitor = new PixelRect(1920, 171, 1366, 768);

        Assert.Equal(monitor, PixelRect.Empty.Union(monitor));
        Assert.Equal(monitor, monitor.Union(PixelRect.Empty));
    }

    [Fact]
    public void Uniao_dos_dois_monitores_desta_maquina()
    {
        var principal = new PixelRect(0, 0, 1920, 1080);
        var secundario = new PixelRect(1920, 171, 1366, 768);

        Assert.Equal(new PixelRect(0, 0, 3286, 1080), principal.Union(secundario));
    }

    [Fact]
    public void Clamp_empurra_para_dentro_sem_encolher()
    {
        var limites = new PixelRect(0, 0, 1920, 1080);
        var selecao = new PixelRect(1800, 1000, 300, 200);

        PixelRect ajustada = selecao.ClampInside(limites);

        Assert.Equal(new PixelRect(1620, 880, 300, 200), ajustada);
        Assert.Equal(selecao.Width, ajustada.Width);
        Assert.Equal(selecao.Height, ajustada.Height);
    }

    [Fact]
    public void Clamp_encolhe_so_quando_nao_cabe()
    {
        var limites = new PixelRect(0, 0, 100, 100);
        var grande = new PixelRect(-50, -50, 500, 500);

        Assert.Equal(limites, grande.ClampInside(limites));
    }

    [Fact]
    public void Contem_outro_retangulo()
    {
        var fora = new PixelRect(0, 0, 100, 100);

        Assert.True(fora.Contains(new PixelRect(10, 10, 50, 50)));
        Assert.True(fora.Contains(new PixelRect(0, 0, 100, 100)));
        Assert.False(fora.Contains(new PixelRect(50, 50, 100, 100)));
        Assert.False(fora.Contains(PixelRect.Empty));
    }

    [Fact]
    public void Tamanho_minimo_e_garantido()
    {
        // Um clique sem arrastar produziria 0x0, que nao da para capturar.
        Assert.Equal(new PixelRect(5, 5, 1, 1), new PixelRect(5, 5, 0, 0).EnsureMinimum(1, 1));
        Assert.Equal(new PixelRect(5, 5, 40, 30), new PixelRect(5, 5, 40, 30).EnsureMinimum(1, 1));
    }

    [Fact]
    public void Offset_move_sem_mudar_o_tamanho()
    {
        Assert.Equal(new PixelRect(15, 25, 100, 50), new PixelRect(10, 20, 100, 50).Offset(5, 5));
    }

    [Fact]
    public void Texto_e_legivel_no_log()
    {
        Assert.Equal("1280x720 em (100, 50)", new PixelRect(100, 50, 1280, 720).ToString());
    }
}
