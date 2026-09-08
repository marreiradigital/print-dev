using System.IO;
using PrintDev.Core.Cloud;
using PrintDev.Core.Infrastructure;

namespace PrintDev.Tests.Cloud;

/// <summary>
/// O registro dos envios é o que torna o botão "Excluir agora" possível: o serviço
/// guarda apenas o digesto do token, então o token em claro só existe aqui.
/// </summary>
public class CloudLinkStoreTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "printdev-links", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public CloudLinkStoreTests()
    {
        _paths = new AppPaths(
            roamingRoot: Path.Combine(_raiz, "Roaming"),
            localRoot: Path.Combine(_raiz, "Local"),
            picturesRoot: Path.Combine(_raiz, "Imagens"));

        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_raiz, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private CloudLinkStore Criar() => new(_paths, Serilog.Core.Logger.None);

    private static CloudLink Link(
        string id = "aaaaBBBBccccDDDD",
        string arquivo = @"C:\Imagens\captura.png",
        double horas = 48)
        => new($"https://exemplo/i/{id}", id, $"token-{id}", DateTimeOffset.UtcNow.AddHours(horas), arquivo);

    [Fact]
    public void Guarda_e_devolve_o_envio()
    {
        CloudLinkStore store = Criar();
        store.Add(Link());

        Assert.Single(store.Items);
        Assert.Equal("aaaaBBBBccccDDDD", store.Items[0].Id);
        Assert.Equal("token-aaaaBBBBccccDDDD", store.Items[0].Token);
    }

    [Fact]
    public void Sobrevive_a_um_reinicio()
    {
        Criar().Add(Link());

        // Instância nova, sem nada em memória: é o cenário de "fechei e abri de novo".
        Assert.Single(Criar().Items);
    }

    /// <summary>
    /// Envio vencido não aparece: oferecer "excluir agora" para algo que já morreu
    /// sozinho seria um botão que não faz nada.
    /// </summary>
    [Fact]
    public void Envio_vencido_some_da_lista()
    {
        CloudLinkStore store = Criar();
        store.Add(Link(id: "vivo0000vivo0000", horas: 10));
        store.Add(Link(id: "morto000morto000", horas: -1));

        Assert.Single(store.Items);
        Assert.Equal("vivo0000vivo0000", store.Items[0].Id);
    }

    [Fact]
    public void Acha_o_envio_pelo_arquivo_da_captura()
    {
        CloudLinkStore store = Criar();
        store.Add(Link(arquivo: @"C:\Imagens\PrintDev_2026.png"));

        Assert.NotNull(store.ParaArquivo(@"C:\Imagens\PrintDev_2026.png"));

        // O Windows não diferencia maiúsculas em caminho; o registro também não pode.
        Assert.NotNull(store.ParaArquivo(@"c:\imagens\printdev_2026.PNG"));

        Assert.Null(store.ParaArquivo(@"C:\Imagens\outra.png"));
        Assert.Null(store.ParaArquivo(null));
        Assert.Null(store.ParaArquivo("   "));
    }

    [Fact]
    public void Arquivo_com_envio_vencido_nao_e_encontrado()
    {
        CloudLinkStore store = Criar();
        store.Add(Link(arquivo: @"C:\Imagens\velha.png", horas: -2));

        Assert.Null(store.ParaArquivo(@"C:\Imagens\velha.png"));
    }

    [Fact]
    public void Reenviar_a_mesma_imagem_substitui_a_entrada_em_vez_de_duplicar()
    {
        CloudLinkStore store = Criar();
        store.Add(Link());
        store.Add(Link());

        Assert.Single(store.Items);
    }

    [Fact]
    public void Esquecer_um_envio_tira_ele_da_lista()
    {
        CloudLinkStore store = Criar();
        store.Add(Link(id: "aaaaBBBBccccDDDD"));
        store.Add(Link(id: "eeeeFFFFgggghhhh"));

        store.Remove("aaaaBBBBccccDDDD");

        Assert.Single(store.Items);
        Assert.Equal("eeeeFFFFgggghhhh", store.Items[0].Id);
    }

    [Fact]
    public void Esquecer_o_que_nao_existe_nao_faz_nada()
    {
        CloudLinkStore store = Criar();
        store.Add(Link());

        store.Remove("naoExisteNaoExis");
        store.Remove("");

        Assert.Single(store.Items);
    }

    /// <summary>
    /// Arquivo corrompido devolve lista vazia em vez de derrubar o programa. A perda
    /// é só a capacidade de excluir antes da hora — as imagens expiram sozinhas.
    /// </summary>
    [Fact]
    public void Arquivo_corrompido_vira_lista_vazia()
    {
        File.WriteAllText(Path.Combine(_paths.SettingsDirectory, "nuvem.json"), "{ isto nao e json ]");

        Assert.Empty(Criar().Items);
    }
}
