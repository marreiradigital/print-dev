using PrintDev.Core.Screens;

namespace PrintDev.Tests.Screens;

/// <summary>
/// Os casos usam a topologia REAL da máquina de desenvolvimento: dois monitores
/// desalinhados na vertical, com um buraco dentro do retângulo que envolve os dois.
/// Testar com uma topologia arrumadinha esconderia justamente o que quebra.
/// </summary>
public class VirtualDesktopTests
{
    /// <summary>1920x1080 na origem.</summary>
    private static readonly MonitorInfo Principal = new(
        new IntPtr(1), @"\\.\DISPLAY2",
        new PixelRect(0, 0, 1920, 1080),
        new PixelRect(0, 0, 1920, 1032),
        96, 96, IsPrimary: true);

    /// <summary>1366x768 à direita, deslocado 171 pixels para baixo.</summary>
    private static readonly MonitorInfo Secundario = new(
        new IntPtr(2), @"\\.\DISPLAY1",
        new PixelRect(1920, 171, 1366, 768),
        new PixelRect(1920, 171, 1366, 768),
        96, 96, IsPrimary: false);

    private static readonly MonitorInfo[] Ambos = [Principal, Secundario];

    [Fact]
    public void Retangulo_que_envolve_os_dois()
    {
        Assert.Equal(new PixelRect(0, 0, 3286, 1080), VirtualDesktop.BoundingBoxOf(Ambos));
    }

    [Fact]
    public void Sem_monitor_o_retangulo_e_vazio()
    {
        Assert.True(VirtualDesktop.BoundingBoxOf([]).IsEmpty);
    }

    [Theory]
    [InlineData(10, 10, @"\\.\DISPLAY2")]
    [InlineData(1919, 1079, @"\\.\DISPLAY2")]
    [InlineData(1920, 171, @"\\.\DISPLAY1")]
    [InlineData(3285, 938, @"\\.\DISPLAY1")]
    public void Ponto_dentro_de_um_monitor(int x, int y, string esperado)
    {
        MonitorInfo? monitor = VirtualDesktop.MonitorAt(Ambos, x, y);

        Assert.NotNull(monitor);
        Assert.Equal(esperado, monitor.DeviceName);
    }

    [Theory]
    [InlineData(2500, 50)]
    [InlineData(2500, 1000)]
    [InlineData(3285, 170)]
    public void Ponto_no_buraco_nao_pertence_a_monitor_nenhum(int x, int y)
    {
        // Estas coordenadas estao DENTRO do retangulo 3286x1080 e FORA das duas telas.
        // Tratar o retangulo que envolve tudo como se fosse area util e o erro classico.
        Assert.True(VirtualDesktop.BoundingBoxOf(Ambos).Contains(x, y));
        Assert.Null(VirtualDesktop.MonitorAt(Ambos, x, y));
    }

    [Fact]
    public void Monitor_mais_proximo_nunca_devolve_nulo_quando_ha_monitores()
    {
        MonitorInfo? monitor = VirtualDesktop.NearestMonitor(Ambos, 2500, 1050);

        Assert.NotNull(monitor);
        Assert.Equal(@"\\.\DISPLAY1", monitor.DeviceName);
    }

    [Fact]
    public void Monitor_mais_proximo_prefere_o_exato_quando_existe()
    {
        MonitorInfo? monitor = VirtualDesktop.NearestMonitor(Ambos, 100, 100);

        Assert.Equal(@"\\.\DISPLAY2", monitor!.DeviceName);
    }

    [Fact]
    public void Buracos_do_desktop_virtual_sao_encontrados()
    {
        IReadOnlyList<PixelRect> buracos = VirtualDesktop.DeadZones(Ambos);

        Assert.NotEmpty(buracos);

        // A soma dos buracos tem que ser exatamente a area do retangulo que envolve tudo
        // menos a area das duas telas.
        long areaTotal = VirtualDesktop.BoundingBoxOf(Ambos).Area;
        long areaDasTelas = Principal.Bounds.Area + Secundario.Bounds.Area;
        long areaDosBuracos = buracos.Sum(b => b.Area);

        Assert.Equal(areaTotal - areaDasTelas, areaDosBuracos);
    }

    [Fact]
    public void Buraco_nenhum_toca_uma_tela()
    {
        foreach (PixelRect buraco in VirtualDesktop.DeadZones(Ambos))
        {
            Assert.False(buraco.IntersectsWith(Principal.Bounds));
            Assert.False(buraco.IntersectsWith(Secundario.Bounds));
        }
    }

    [Fact]
    public void Monitores_perfeitamente_alinhados_nao_deixam_buraco()
    {
        MonitorInfo direito = Secundario with { Bounds = new PixelRect(1920, 0, 1920, 1080) };

        Assert.Empty(VirtualDesktop.DeadZones([Principal, direito]));
    }

    [Fact]
    public void Um_monitor_so_nao_deixa_buraco()
    {
        Assert.Empty(VirtualDesktop.DeadZones([Principal]));
    }

    [Fact]
    public void Conversao_para_unidades_do_wpf_desconta_a_origem_do_monitor()
    {
        // Um ponto no canto do monitor secundario e (0,0) DENTRO da janela dele, ainda
        // que seja (1920,171) no desktop virtual. Errar isso desloca todo o desenho.
        (double x, double y) = Secundario.ToDeviceIndependent(1920, 171);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }

    [Fact]
    public void Conversao_respeita_a_escala_do_monitor()
    {
        MonitorInfo em150 = Secundario with { DpiX = 144, DpiY = 144 };

        (double x, double y) = em150.ToDeviceIndependent(1920 + 300, 171 + 150);

        Assert.Equal(200, x);
        Assert.Equal(100, y);
    }

    [Fact]
    public void Ida_e_volta_da_conversao_de_escala()
    {
        MonitorInfo em150 = Secundario with { DpiX = 144, DpiY = 144 };

        (double dipX, double dipY) = em150.ToDeviceIndependent(2220, 321);
        (int x, int y) = em150.ToPhysical(dipX, dipY);

        Assert.Equal(2220, x);
        Assert.Equal(321, y);
    }
}
