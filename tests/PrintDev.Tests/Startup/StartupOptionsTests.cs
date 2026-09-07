using PrintDev.Core.Startup;

namespace PrintDev.Tests.Startup;

public class StartupOptionsTests
{
    [Fact]
    public void Sem_argumentos_devolve_o_padrao()
    {
        StartupOptions options = StartupOptions.Parse([]);

        Assert.False(options.Silent);
        Assert.False(options.OpenSettings);
        Assert.Equal(StartupCommand.Run, options.Command);
        Assert.Empty(options.Unrecognized);
    }

    [Fact]
    public void Args_nulo_nao_estoura()
    {
        Assert.Equal(StartupOptions.Default, StartupOptions.Parse(null));
    }

    [Theory]
    [InlineData("--silencioso")]
    [InlineData("--silent")]
    [InlineData("-s")]
    [InlineData("/silencioso")]
    [InlineData("--SILENCIOSO")]
    [InlineData("  --Silencioso  ")]
    public void Reconhece_modo_silencioso_em_todas_as_convencoes(string argument)
    {
        Assert.True(StartupOptions.Parse([argument]).Silent);
    }

    [Theory]
    [InlineData("--configuracoes")]
    [InlineData("--settings")]
    [InlineData("/c")]
    public void Reconhece_abertura_das_configuracoes(string argument)
    {
        Assert.True(StartupOptions.Parse([argument]).OpenSettings);
    }

    [Theory]
    [InlineData("--install-elevated-task", StartupCommand.InstallElevatedTask)]
    [InlineData("--instalar-tarefa-elevada", StartupCommand.InstallElevatedTask)]
    [InlineData("--uninstall-elevated-task", StartupCommand.UninstallElevatedTask)]
    [InlineData("--remover-tarefa-elevada", StartupCommand.UninstallElevatedTask)]
    public void Reconhece_os_comandos_de_tarefa_elevada(string argument, StartupCommand expected)
    {
        Assert.Equal(expected, StartupOptions.Parse([argument]).Command);
    }

    [Fact]
    public void Argumento_desconhecido_e_preservado_e_nao_derruba()
    {
        // O shell do Windows pode passar coisas que nao sao nossas; isso vai para o log,
        // nunca para uma excecao.
        StartupOptions options = StartupOptions.Parse(["--silencioso", "--coisa-estranha", @"C:\algum\caminho.png"]);

        Assert.True(options.Silent);
        Assert.Equal(["--coisa-estranha", @"C:\algum\caminho.png"], options.Unrecognized);
    }

    [Fact]
    public void Ignora_entradas_vazias_ou_so_com_espaco()
    {
        StartupOptions options = StartupOptions.Parse(["", "   ", "--silencioso"]);

        Assert.True(options.Silent);
        Assert.Empty(options.Unrecognized);
    }

    [Fact]
    public void Combina_varias_flags_na_mesma_chamada()
    {
        StartupOptions options = StartupOptions.Parse(["--silencioso", "--configuracoes"]);

        Assert.True(options.Silent);
        Assert.True(options.OpenSettings);
        Assert.Equal(StartupCommand.Run, options.Command);
    }
}
