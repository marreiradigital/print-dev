using PrintDev.Core.Updates;

namespace PrintDev.Tests.Updates;

/// <summary>
/// A comparação de versões é a decisão mais perigosa do sistema de atualização, e as
/// duas maneiras de errar são silenciosas: para um lado, o programa se reinstala em
/// laço; para o outro, nunca mais se atualiza e ninguém percebe.
/// </summary>
public class ReleaseTagTests
{
    [Theory]
    [InlineData("v0.1.3", 0, 1, 3)]
    [InlineData("0.1.3", 0, 1, 3)]
    [InlineData("V2.0.0", 2, 0, 0)]
    [InlineData("  v1.4.7  ", 1, 4, 7)]
    [InlineData("v1.2", 1, 2, 0)]
    [InlineData("v10.20.30", 10, 20, 30)]
    public void Le_a_etiqueta_com_e_sem_o_v(string tag, int maior, int menor, int correcao)
    {
        Assert.True(ReleaseTag.TryParse(tag, out Version? versao));
        Assert.Equal(new Version(maior, menor, correcao), versao);
    }

    /// <summary>
    /// O quarto componente vem do assembly, não da etiqueta. Cortá-lo é o que faz
    /// <c>0.1.2.0</c> e <c>0.1.2</c> serem a mesma coisa — sem isso, o programa se
    /// consideraria desatualizado em relação a si mesmo, para sempre.
    /// </summary>
    [Fact]
    public void Corta_o_quarto_componente()
    {
        Assert.True(ReleaseTag.TryParse("v1.2.3.4", out Version? versao));
        Assert.Equal(new Version(1, 2, 3), versao);
    }

    /// <summary>
    /// O sufixo de pré-lançamento é ignorado no número: quem decide se um lançamento
    /// é pré-lançamento é o campo próprio da API, não um palpite sobre o texto.
    /// </summary>
    [Theory]
    [InlineData("v0.2.0-rc1")]
    [InlineData("v0.2.0-beta.3")]
    [InlineData("v0.2.0+build.99")]
    public void Ignora_o_sufixo_de_pre_lancamento(string tag)
    {
        Assert.True(ReleaseTag.TryParse(tag, out Version? versao));
        Assert.Equal(new Version(0, 2, 0), versao);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("v1.")]
    [InlineData("v.1.2")]
    [InlineData("v1.2.3.4.5")]
    [InlineData("v1.-2.3")]
    [InlineData("v1.2.x")]
    [InlineData("v 1.2.3")]
    public void Recusa_o_que_nao_e_versao(string? tag)
    {
        Assert.False(ReleaseTag.TryParse(tag, out Version? versao));
        Assert.Null(versao);
    }

    [Fact]
    public void So_vale_o_que_e_estritamente_maior()
    {
        var atual = new Version(0, 1, 2);

        Assert.True(ReleaseTag.Vale(new Version(0, 1, 3), atual));
        Assert.True(ReleaseTag.Vale(new Version(0, 2, 0), atual));
        Assert.True(ReleaseTag.Vale(new Version(1, 0, 0), atual));

        Assert.False(ReleaseTag.Vale(atual, atual));
        Assert.False(ReleaseTag.Vale(new Version(0, 1, 1), atual));
        Assert.False(ReleaseTag.Vale(new Version(0, 0, 9), atual));
    }

    /// <summary>
    /// O caso que o teste existe para travar: a versão do assembly tem quatro
    /// componentes e a etiqueta tem três. Comparadas cruas, <c>0.1.2</c> seria
    /// MENOR que <c>0.1.2.0</c> — e o programa ofereceria a si mesmo como novidade
    /// a cada doze horas, para sempre.
    /// </summary>
    [Fact]
    public void A_versao_do_assembly_com_quatro_componentes_nao_confunde_a_comparacao()
    {
        var doAssembly = new Version(0, 1, 2, 0);

        Assert.True(ReleaseTag.TryParse("v0.1.2", out Version? daEtiqueta));

        Assert.False(ReleaseTag.Vale(daEtiqueta!, doAssembly));
        Assert.False(ReleaseTag.Vale(doAssembly, daEtiqueta!));
        Assert.Equal(ReleaseTag.Normalize(doAssembly), daEtiqueta);
    }

    /// <summary>
    /// Versão MENOR nunca é oferecida. Um lançamento despublicado ou uma etiqueta
    /// digitada errada não podem empurrar quem já está em dia para trás.
    /// </summary>
    [Fact]
    public void Nunca_oferece_rebaixamento()
    {
        Assert.False(ReleaseTag.Vale(new Version(0, 1, 0), new Version(0, 9, 9)));
    }

    [Fact]
    public void Normaliza_versao_sem_o_terceiro_componente()
    {
        Assert.Equal(new Version(3, 4, 0), ReleaseTag.Normalize(new Version(3, 4)));
    }

    /// <summary>
    /// A versão em execução tem de ser legível: se o assembly não trouxer versão, o
    /// resto do sistema compara contra lixo sem nunca reclamar.
    /// </summary>
    [Fact]
    public void A_versao_atual_tem_tres_componentes()
    {
        Assert.Equal(ReleaseTag.Current, ReleaseTag.Normalize(ReleaseTag.Current));
        Assert.True(ReleaseTag.Current.Build >= 0);
        Assert.Equal(-1, ReleaseTag.Current.Revision);
    }
}
