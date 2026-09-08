using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Serilog;

namespace PrintDev.Core.Updates;

/// <summary>O que a consulta ao GitHub devolveu, antes de virar decisão.</summary>
/// <param name="NotModified">
/// O servidor respondeu 304: nada mudou desde a última vez. Vale ouro porque resposta
/// condicional com 304 <b>não consome a cota</b> de requisições anônimas.
/// </param>
public sealed record ReleaseQuery(
    bool NotModified,
    AvailableUpdate? Release,
    string? ETag,
    string? Error);

/// <summary>
/// Lê os lançamentos publicados do repositório.
/// <para>
/// Anônimo de propósito: exigir um token para o programa saber que existe versão nova
/// seria pedir ao usuário uma credencial para receber uma atualização. A cota anônima é
/// de 60 requisições por hora por endereço; a duas consultas por dia, sobra tudo.
/// </para>
/// </summary>
public sealed class GitHubReleaseClient
{
    private const string ApiRoot = "https://api.github.com";

    /// <summary>Prefixo do arquivo que interessa entre os anexos do lançamento.</summary>
    private const string InstallerPrefix = "PrintDev-Setup";

    private readonly HttpClient _http;
    private readonly ILogger _log;

    public GitHubReleaseClient(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log.ForContext<GitHubReleaseClient>();
    }

    /// <summary>
    /// Procura o lançamento mais novo do repositório.
    /// </summary>
    /// <param name="repository">No formato <c>dono/nome</c>.</param>
    /// <param name="includePrereleases">Também considerar pré-lançamentos.</param>
    /// <param name="etag">Validador da consulta anterior, ou <see langword="null"/>.</param>
    public async Task<ReleaseQuery> FindLatestAsync(
        string repository,
        bool includePrereleases,
        string? etag,
        CancellationToken cancellation = default)
    {
        if (!EhRepositorioValido(repository))
        {
            return new ReleaseQuery(false, null, null, "O repositório configurado não está no formato dono/nome.");
        }

        // Sem pré-lançamento, /releases/latest já devolve exatamente o que se quer e
        // custa uma resposta pequena. Com pré-lançamento não há atalho: /latest os
        // ignora por definição, então é preciso listar e escolher.
        string caminho = includePrereleases
            ? $"{ApiRoot}/repos/{repository}/releases?per_page=10"
            : $"{ApiRoot}/repos/{repository}/releases/latest";

        // O HttpClient é compartilhado com o download do instalador, que pode levar
        // minutos numa conexão ruim, então ele não tem teto próprio. Consultar
        // metadado é outra coisa: se não respondeu em vinte segundos, não vai responder.
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limite.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = limite.Token;

        try
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Get, caminho);

            if (!string.IsNullOrWhiteSpace(etag))
            {
                pedido.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag, isWeak: etag.StartsWith("W/", StringComparison.Ordinal)));
            }

            using HttpResponseMessage resposta = await _http
                .SendAsync(pedido, HttpCompletionOption.ResponseContentRead, token)
                .ConfigureAwait(false);

            if (resposta.StatusCode == HttpStatusCode.NotModified)
            {
                _log.Debug("GitHub respondeu 304: nada novo desde a última consulta.");
                return new ReleaseQuery(true, null, etag, null);
            }

            if (resposta.StatusCode == HttpStatusCode.NotFound)
            {
                return new ReleaseQuery(false, null, null, "O repositório não foi encontrado ou ainda não tem nenhum lançamento.");
            }

            if (resposta.StatusCode == (HttpStatusCode)403 || resposta.StatusCode == (HttpStatusCode)429)
            {
                // A cota anônima é por endereço: numa rede corporativa, o vizinho gasta.
                return new ReleaseQuery(false, null, null, "O GitHub recusou a consulta por excesso de requisições. Tente mais tarde.");
            }

            if (!resposta.IsSuccessStatusCode)
            {
                return new ReleaseQuery(false, null, null, $"O GitHub respondeu {(int)resposta.StatusCode}.");
            }

            string json = await resposta.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            string? novaEtag = resposta.Headers.ETag?.ToString();

            AvailableUpdate? achado = includePrereleases
                ? EscolherDaLista(json)
                : Ler(JsonDocument.Parse(json).RootElement);

            return new ReleaseQuery(false, achado, novaEtag, null);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new ReleaseQuery(false, null, null, "A consulta ao GitHub demorou demais.");
        }
        catch (HttpRequestException excecao)
        {
            _log.Debug(excecao, "Falha de rede ao consultar lançamentos");
            return new ReleaseQuery(false, null, null, "Não foi possível falar com o GitHub. Verifique a conexão.");
        }
        catch (JsonException excecao)
        {
            _log.Warning(excecao, "Resposta do GitHub não é o JSON esperado");
            return new ReleaseQuery(false, null, null, "A resposta do GitHub veio em formato inesperado.");
        }
    }

    /// <summary>Entre vários lançamentos, o de maior versão que não seja rascunho.</summary>
    private static AvailableUpdate? EscolherDaLista(string json)
    {
        using JsonDocument documento = JsonDocument.Parse(json);

        if (documento.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        AvailableUpdate? melhor = null;

        foreach (JsonElement item in documento.RootElement.EnumerateArray())
        {
            AvailableUpdate? candidato = Ler(item);

            if (candidato is not null && (melhor is null || candidato.Version > melhor.Version))
            {
                melhor = candidato;
            }
        }

        return melhor;
    }

    /// <summary>
    /// Extrai o que interessa de um lançamento. Devolve <see langword="null"/> para
    /// rascunho, etiqueta que não é versão, ou lançamento sem instalador anexado —
    /// os três casos em que não há o que oferecer.
    /// </summary>
    private static AvailableUpdate? Ler(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (release.TryGetProperty("draft", out JsonElement rascunho) && rascunho.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        string? tag = Texto(release, "tag_name");
        if (!ReleaseTag.TryParse(tag, out Version? versao))
        {
            return null;
        }

        if (!release.TryGetProperty("assets", out JsonElement anexos) || anexos.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? escolhido = null;

        foreach (JsonElement anexo in anexos.EnumerateArray())
        {
            string nome = Texto(anexo, "name") ?? string.Empty;

            if (!nome.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // O instalador nomeado vence sempre; qualquer outro .exe só serve de
            // reserva, para o caso de o padrão de nome mudar num lançamento futuro.
            if (nome.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                escolhido = anexo;
                break;
            }

            escolhido ??= anexo;
        }

        if (escolhido is null)
        {
            return null;
        }

        JsonElement anexoFinal = escolhido.Value;
        string? url = Texto(anexoFinal, "browser_download_url");

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        long tamanho = anexoFinal.TryGetProperty("size", out JsonElement t) && t.TryGetInt64(out long n) ? n : 0;

        return new AvailableUpdate(
            Version: versao,
            Tag: tag!,
            Notes: Texto(release, "body") ?? string.Empty,
            DownloadUrl: url!,
            AssetName: Texto(anexoFinal, "name") ?? "PrintDev-Setup.exe",
            SizeBytes: tamanho,
            Sha256: LerDigesto(anexoFinal));
    }

    /// <summary>
    /// O campo <c>digest</c> vem como <c>sha256:abc…</c>. Qualquer outro algoritmo é
    /// descartado em vez de aceito às cegas: conferir com o algoritmo errado é o mesmo
    /// que não conferir, mas parece que conferiu.
    /// </summary>
    private static string? LerDigesto(JsonElement anexo)
    {
        string? bruto = Texto(anexo, "digest");

        if (string.IsNullOrWhiteSpace(bruto))
        {
            return null;
        }

        const string prefixo = "sha256:";

        return bruto.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)
            ? bruto[prefixo.Length..].Trim()
            : null;
    }

    private static string? Texto(JsonElement objeto, string propriedade)
        => objeto.TryGetProperty(propriedade, out JsonElement valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;

    /// <summary>
    /// Confere o formato <c>dono/nome</c> antes de montar a URL. Sem isto, um valor
    /// com barra a mais no arquivo de configuração viraria uma requisição para um
    /// caminho arbitrário da API.
    /// </summary>
    private static bool EhRepositorioValido(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        string[] partes = repository.Split('/');

        return partes.Length == 2
            && partes.All(p => p.Length is > 0 and <= 100
                               && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
    }
}
