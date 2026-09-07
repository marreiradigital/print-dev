using System.IO;
using System.Runtime.InteropServices;
using PrintDev.Core.Storage;

namespace PrintDev.Tests.Storage;

/// <summary>
/// Testes da única parte do programa que remove arquivo do usuário.
/// <para>
/// O primeiro deles existe por um defeito real: a estrutura passada ao shell tinha
/// <c>Pack = 1</c>, herdado de exemplo de 32 bits, e em 64 bits isso derrubava o
/// programa inteiro com <c>AccessViolationException</c> — que o .NET não deixa capturar,
/// então não sobrava nem linha de log. O usuário clicava em "Desfazer" e o Print Dev
/// simplesmente sumia.
/// </para>
/// </summary>
public class RecycleBinTests
{
    /// <summary>
    /// Confere o alinhamento contra o tamanho que <c>SHFILEOPSTRUCTW</c> tem em 64 bits.
    /// <para>
    /// É a verificação mais barata que existe para essa classe de erro, e a única que
    /// falha de forma legível: um campo fora de lugar não dá exceção, dá corrupção de
    /// memória.
    /// </para>
    /// </summary>
    [Fact]
    public void A_estrutura_do_shell_tem_o_alinhamento_de_64_bits()
    {
        Assert.Equal(
            RecycleBin.TamanhoEsperadoDaEstrutura,
            Marshal.SizeOf<RecycleBin.SHFILEOPSTRUCT>());
    }

    [Fact]
    public void Manda_um_arquivo_de_verdade_para_a_Lixeira()
    {
        string arquivo = Path.Combine(
            Path.GetTempPath(),
            $"PrintDev-teste-{Guid.NewGuid():N}.png");

        File.WriteAllText(arquivo, "conteúdo de teste");
        Assert.True(File.Exists(arquivo));

        bool foi = RecycleBin.Send(arquivo, out string? motivo);

        Assert.True(foi, motivo);
        Assert.Null(motivo);
        Assert.False(File.Exists(arquivo));
    }

    [Fact]
    public void Acentos_e_espacos_no_nome_nao_atrapalham()
    {
        string arquivo = Path.Combine(
            Path.GetTempPath(),
            $"Captura de tela — ação {Guid.NewGuid():N}.png");

        File.WriteAllText(arquivo, "conteúdo");

        Assert.True(RecycleBin.Send(arquivo, out string? motivo), motivo);
        Assert.False(File.Exists(arquivo));
    }

    /// <summary>
    /// Arquivo que já não existe conta como sucesso: o resultado desejado é o mesmo, e
    /// tratar como falha faria o "Desfazer" reclamar de um trabalho que já estava feito.
    /// </summary>
    [Fact]
    public void Arquivo_inexistente_conta_como_sucesso()
    {
        string arquivo = Path.Combine(Path.GetTempPath(), $"nao-existe-{Guid.NewGuid():N}.png");

        Assert.True(RecycleBin.Send(arquivo, out string? motivo));
        Assert.Null(motivo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Caminho_vazio_falha_com_motivo_escrito(string? caminho)
    {
        Assert.False(RecycleBin.Send(caminho!, out string? motivo));
        Assert.Equal("caminho vazio", motivo);
    }

    /// <summary>
    /// Caminho relativo é resolvido antes de chegar ao shell, que não os entende — e
    /// falharia de um jeito difícil de diagnosticar em vez de dizer o que houve.
    /// </summary>
    [Fact]
    public void Caminho_relativo_e_resolvido_antes_de_ir_para_o_shell()
    {
        string pasta = Path.Combine(Path.GetTempPath(), $"printdev-rel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(pasta);

        string anterior = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(pasta);
            File.WriteAllText(Path.Combine(pasta, "relativo.png"), "x");

            Assert.True(RecycleBin.Send("relativo.png", out string? motivo), motivo);
            Assert.False(File.Exists(Path.Combine(pasta, "relativo.png")));
        }
        finally
        {
            Directory.SetCurrentDirectory(anterior);
            Directory.Delete(pasta, recursive: true);
        }
    }

    /// <summary>
    /// Caminho malformado tem de voltar como falha explicada, nunca como corrupção de
    /// memória — que é o que a versão anterior fazia com qualquer entrada.
    /// </summary>
    [Fact]
    public void Caminho_invalido_falha_com_motivo_em_vez_de_derrubar_o_processo()
    {
        Assert.False(RecycleBin.Send("Z:\\<>|caminho\0invalido.png", out string? motivo));
        Assert.False(string.IsNullOrWhiteSpace(motivo));
    }
}
