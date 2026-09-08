using System.Net;
using System.Net.Http;
using PrintDev.Core.Updates;

namespace PrintDev.Tests.Updates;

/// <summary>
/// O cliente é testado contra respostas fabricadas, e não contra o GitHub: o valor
/// aqui é provar o que acontece nos casos que a rede raramente entrega na hora do
/// teste — 304, 403 por cota, anexo sem digesto, lançamento sem instalador.
/// </summary>
public class GitHubReleaseClientTests
{
    private const string Repositorio = "marreiradigital/print-dev";

    /// <summary>Resposta de um lançamento normal, como a API devolve.</summary>
    private static string Lancamento(
        string tag = "v0.9.0",
        string ativo = "PrintDev-Setup-0.9.0.exe",
        string? digesto = "sha256:aabbcc",
        bool rascunho = false)
        => $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(rascunho ? "true" : "false")}},
          "prerelease": false,
          "body": "## O que mudou\n\n- coisas",
          "assets": [
            {
              "name": "{{ativo}}",
              "size": 6939314,
              "browser_download_url": "https://github.com/x/y/releases/download/{{tag}}/{{ativo}}"
              {{(digesto is null ? "" : $", \"digest\": \"{digesto}\"")}}
            }
          ]
        }
        """;

    private static GitHubReleaseClient Cliente(
        HttpStatusCode status,
        string corpo,
        string? etag = null,
        Action<HttpRequestMessage>? inspecionar = null)
    {
        var handler = new RespostaFixa(status, corpo, etag, inspecionar);
        return new GitHubReleaseClient(new HttpClient(handler), Serilog.Core.Logger.None);
    }

    [Fact]
    public async Task Le_a_versao_o_instalador_e_o_digesto()
    {
        GitHubReleaseClient cliente = Cliente(HttpStatusCode.OK, Lancamento());

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, null);

        Assert.Null(consulta.Error);
        Assert.NotNull(consulta.Release);
        Assert.Equal(new Version(0, 9, 0), consulta.Release!.Version);
        Assert.Equal("PrintDev-Setup-0.9.0.exe", consulta.Release.AssetName);
        Assert.Equal(6939314, consulta.Release.SizeBytes);

        // O prefixo "sha256:" fica de fora: quem confere compara hexadecimal puro.
        Assert.Equal("aabbcc", consulta.Release.Sha256);
    }

    /// <summary>
    /// Digesto de outro algoritmo é descartado em vez de aceito. Conferir com o
    /// algoritmo errado é o mesmo que não conferir — só que parecendo que conferiu.
    /// </summary>
    [Fact]
    public async Task Descarta_digesto_que_nao_seja_sha256()
    {
        GitHubReleaseClient cliente = Cliente(HttpStatusCode.OK, Lancamento(digesto: "md5:aabbcc"));

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, null);

        Assert.NotNull(consulta.Release);
        Assert.Null(consulta.Release!.Sha256);
    }

    [Fact]
    public async Task Lancamento_em_rascunho_nao_e_oferecido()
    {
        GitHubReleaseClient cliente = Cliente(HttpStatusCode.OK, Lancamento(rascunho: true));

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, null);

        Assert.Null(consulta.Release);
        Assert.Null(consulta.Error);
    }

    [Fact]
    public async Task Lancamento_sem_instalador_nao_e_oferecido()
    {
        GitHubReleaseClient cliente = Cliente(HttpStatusCode.OK, Lancamento(ativo: "notas.pdf"));

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, null);

        Assert.Null(consulta.Release);
    }

    [Fact]
    public async Task Etiqueta_que_nao_e_versao_nao_e_oferecida()
    {
        GitHubReleaseClient cliente = Cliente(HttpStatusCode.OK, Lancamento(tag: "sem-versao"));

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, null);

        Assert.Null(consulta.Release);
    }

    /// <summary>
    /// O 304 é o desfecho mais comum na vida real — e o mais valioso, porque resposta
    /// condicional com 304 não consome a cota anônima de 60 requisições por hora.
    /// </summary>
    [Fact]
    public async Task Trezentos_e_quatro_devolve_nao_modificado_e_preserva_o_validador()
    {
        HttpRequestMessage? enviado = null;
        GitHubReleaseClient cliente = Cliente(
            HttpStatusCode.NotModified, string.Empty, inspecionar: p => enviado = p);

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, "\"abc123\"");

        Assert.True(consulta.NotModified);
        Assert.Null(consulta.Error);
        Assert.Equal("\"abc123\"", consulta.ETag);

        // E o validador precisa realmente ter ido no pedido, senão o 304 nunca vem.
        Assert.NotNull(enviado);
        Assert.Contains(enviado!.Headers.IfNoneMatch, e => e.Tag == "\"abc123\"");
    }

    [Fact]
    public async Task Cota_estourada_vira_frase_em_portugues_e_nao_excecao()
    {
        GitHubReleaseClient cliente = Cliente((HttpStatusCode)403, "{}");

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, null);

        Assert.Null(consulta.Release);
        Assert.NotNull(consulta.Error);
        Assert.Contains("excesso de requisições", consulta.Error!);
    }

    [Fact]
    public async Task Repositorio_inexistente_explica_em_vez_de_falhar_feio()
    {
        GitHubReleaseClient cliente = Cliente(HttpStatusCode.NotFound, "{}");

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, null);

        Assert.NotNull(consulta.Error);
        Assert.Contains("não foi encontrado", consulta.Error!);
    }

    [Fact]
    public async Task Resposta_que_nao_e_json_nao_derruba_o_programa()
    {
        GitHubReleaseClient cliente = Cliente(HttpStatusCode.OK, "<html>portal cativo do wifi</html>");

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, false, null);

        Assert.Null(consulta.Release);
        Assert.NotNull(consulta.Error);
    }

    /// <summary>
    /// Um valor com barra a mais no arquivo de configuração não pode virar requisição
    /// para um caminho arbitrário da API do GitHub.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("sem-barra")]
    [InlineData("dono/nome/extra")]
    [InlineData("dono/../../outra-coisa")]
    [InlineData("dono/nome?x=1")]
    [InlineData("dono /nome")]
    public async Task Repositorio_malformado_e_recusado_antes_de_virar_requisicao(string repositorio)
    {
        bool bateu = false;
        GitHubReleaseClient cliente = Cliente(HttpStatusCode.OK, Lancamento(), inspecionar: _ => bateu = true);

        ReleaseQuery consulta = await cliente.FindLatestAsync(repositorio, false, null);

        Assert.NotNull(consulta.Error);
        Assert.False(bateu);
    }

    /// <summary>Com pré-lançamentos, a escolha é pela maior versão da lista.</summary>
    [Fact]
    public async Task Entre_varios_lancamentos_escolhe_a_maior_versao()
    {
        string lista = $"[{Lancamento(tag: "v0.9.0", ativo: "PrintDev-Setup-0.9.0.exe")}," +
                       $"{Lancamento(tag: "v1.1.0", ativo: "PrintDev-Setup-1.1.0.exe")}," +
                       $"{Lancamento(tag: "v1.0.5", ativo: "PrintDev-Setup-1.0.5.exe")}]";

        GitHubReleaseClient cliente = Cliente(HttpStatusCode.OK, lista);

        ReleaseQuery consulta = await cliente.FindLatestAsync(Repositorio, includePrereleases: true, null);

        Assert.NotNull(consulta.Release);
        Assert.Equal(new Version(1, 1, 0), consulta.Release!.Version);
    }

    /// <summary>Devolve uma resposta pronta e deixa o pedido disponível para conferência.</summary>
    private sealed class RespostaFixa(
        HttpStatusCode status,
        string corpo,
        string? etag,
        Action<HttpRequestMessage>? inspecionar) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            inspecionar?.Invoke(request);

            var resposta = new HttpResponseMessage(status)
            {
                Content = new StringContent(corpo),
            };

            if (etag is not null)
            {
                resposta.Headers.TryAddWithoutValidation("ETag", etag);
            }

            return Task.FromResult(resposta);
        }
    }
}
