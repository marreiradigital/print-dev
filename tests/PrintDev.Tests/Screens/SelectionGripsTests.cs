using PrintDev.Core.Screens;

namespace PrintDev.Tests.Screens;

public class SelectionGripsTests
{
    private static readonly PixelRect Selection = new(100, 100, 200, 100);
    private static readonly PixelRect Desktop = new(0, 0, 3286, 1080);

    [Theory]
    [InlineData(100, 100, SelectionGrip.TopLeft)]
    [InlineData(299, 100, SelectionGrip.TopRight)]
    [InlineData(100, 199, SelectionGrip.BottomLeft)]
    [InlineData(299, 199, SelectionGrip.BottomRight)]
    [InlineData(103, 150, SelectionGrip.Left)]
    [InlineData(200, 96, SelectionGrip.Top)]
    [InlineData(304, 150, SelectionGrip.Right)]
    [InlineData(200, 203, SelectionGrip.Bottom)]
    [InlineData(200, 150, SelectionGrip.Inside)]
    [InlineData(50, 50, SelectionGrip.None)]
    [InlineData(310, 150, SelectionGrip.None)]
    public void Cada_ponto_pega_a_alca_certa(int x, int y, SelectionGrip expected)
    {
        Assert.Equal(expected, SelectionGrips.HitTest(Selection, x, y, tolerance: 6));
    }

    [Fact]
    public void Selecao_estreita_ainda_deixa_pegar_a_borda_direita()
    {
        // Com 6 px de largura e tolerancia de 6, as duas bordas "estao perto" de qualquer
        // ponto. Vale a mais proxima - senao a direita nunca seria pega.
        var narrow = new PixelRect(100, 100, 6, 100);

        Assert.Equal(SelectionGrip.Right, SelectionGrips.HitTest(narrow, 105, 150, tolerance: 6));
        Assert.Equal(SelectionGrip.Left, SelectionGrips.HitTest(narrow, 100, 150, tolerance: 6));
    }

    [Fact]
    public void Arrastar_por_dentro_move_sem_mudar_o_tamanho_e_sem_sair_da_tela()
    {
        Assert.Equal(new PixelRect(110, 95, 200, 100), SelectionGrips.Drag(Selection, SelectionGrip.Inside, 10, -5, Desktop));
        Assert.Equal(new PixelRect(0, 0, 200, 100), SelectionGrips.Drag(Selection, SelectionGrip.Inside, -500, -500, Desktop));
    }

    [Fact]
    public void Arrastar_o_canto_muda_as_duas_bordas_dele()
    {
        Assert.Equal(
            new PixelRect(100, 100, 230, 140),
            SelectionGrips.Drag(Selection, SelectionGrip.BottomRight, 30, 40, Desktop));

        Assert.Equal(
            new PixelRect(90, 80, 210, 120),
            SelectionGrips.Drag(Selection, SelectionGrip.TopLeft, -10, -20, Desktop));
    }

    [Fact]
    public void Borda_arrastada_para_alem_da_oposta_inverte_em_vez_de_ficar_negativa()
    {
        // A borda esquerda (x = 100) vai para x = 350, alem da direita (x = 300).
        PixelRect flipped = SelectionGrips.Drag(Selection, SelectionGrip.Left, 250, 0, Desktop);

        Assert.Equal(new PixelRect(300, 100, 50, 100), flipped);
        Assert.False(flipped.IsEmpty);
    }

    [Fact]
    public void Borda_encostada_na_oposta_deixa_um_pixel()
    {
        PixelRect thin = SelectionGrips.Drag(Selection, SelectionGrip.Right, -200, 0, Desktop);

        Assert.Equal(1, thin.Width);
    }

    [Fact]
    public void Redimensionar_nao_passa_da_area_de_trabalho()
    {
        PixelRect grown = SelectionGrips.Drag(Selection, SelectionGrip.BottomRight, 9000, 9000, Desktop);

        Assert.Equal(Desktop.Right, grown.Right);
        Assert.Equal(Desktop.Bottom, grown.Bottom);
    }
}
