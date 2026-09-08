using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using PrintDev.Core.Configuration;
using Serilog;

namespace PrintDev.Core.Cloud;

/// <summary>Uma captura publicada, com o que é preciso para apagá-la antes da hora.</summary>
/// <param name="Token">
/// Segredo que autoriza a exclusão. Só existe nesta máquina: o serviço guarda apenas o
/// digesto dele, então perder este arquivo significa esperar a expiração.
/// </param>
public sealed record CloudLink(
    string Url,
    string Id,
    string Token,
    DateTimeOffset ExpiresAt,
    string LocalPath);

/// <summary>Como terminou um envio, com a frase pronta para a interface.</summary>
public sealed record UploadOutcome(bool Success, CloudLink? Link, string Message);

/// <summary>
/// Publica uma captura no serviço temporário e a remove de lá.
/// <para>
/// A única parte do Print Dev que faz um arquivo do usuário sair da máquina. Por isso
/// nunca é chamada por conta própria — sempre a partir de uma ação explícita.
/// </para>
/// </summary>
public sealed class CloudUploader
{
    /// <summary>
    /// Teto do lado do cliente, igual ao do serviço. Existe para dar a mensagem certa
    /// na hora certa, em vez de subir dez megabytes para ouvir um 413.
    /// </summary>
    public const long MaxBytes = 10L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;

    public CloudUploader(HttpClient http, ISettingsService settings, ILogger log)
    {
        _http = http;
        _settings = settings;
        _log = log.ForContext<CloudUploader>();
    }

    /// <summary>
    /// A chave gravada na compilação. Vem por metadado de assembly, e não por constante
    /// no código, porque o repositório é público e a chave não pode entrar nele.
    /// </summary>
    private static string? ChaveDeCompilacao { get; } = Assembly.GetEntryAssembly()
        ?.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "ChaveDaNuvem")
        ?.Value;

    /// <summary>Se há chave para usar — sem uma, o botão não deve nem aparecer.</summary>
    public bool EstaConfigurado => !string.IsNullOrWhiteSpace(Chave());

    /// <summary>Publica o arquivo e devolve o link temporário.</summary>
    public async Task<UploadOutcome> UploadAsync(string filePath, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        CloudSettings config = _settings.Current.Cloud;
        string? chave = Chave();

        if (string.IsNullOrWhiteSpace(chave))
        {
            return new UploadOutcome(false, null, "Esta compilação não tem chave de envio configurada.");
        }

        if (!File.Exists(filePath))
        {
            return new UploadOutcome(false, null, "O arquivo da captura não existe mais.");
        }

        var arquivo = new FileInfo(filePath);

        if (arquivo.Length > MaxBytes)
        {
            return new UploadOutcome(
                false,
                null,
                $"A imagem tem {arquivo.Length / 1024 / 1024} MB e o limite é {MaxBytes / 1024 / 1024} MB.");
        }

        string? tipo = TipoDe(filePath);

        if (tipo is null)
        {
            return new UploadOutcome(false, null, "Só dá para enviar PNG, JPEG ou WebP.");
        }

        if (!TryMontarUrl(config.Endpoint, "novo", out Uri? destino))
        {
            return new UploadOutcome(false, null, "O endereço do serviço de nuvem não é uma URL válida.");
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limite.CancelAfter(TimeSpan.FromMinutes(2));

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath, limite.Token).ConfigureAwait(false);

            using var conteudo = new ByteArrayContent(bytes);
            conteudo.Headers.ContentType = new MediaTypeHeaderValue(tipo);

            using var pedido = new HttpRequestMessage(HttpMethod.Post, destino) { Content = conteudo };
            pedido.Headers.TryAddWithoutValidation("x-chave", chave);

            using HttpResponseMessage resposta = await _http
                .SendAsync(pedido, limite.Token)
                .ConfigureAwait(false);

            string corpo = await resposta.Content.ReadAsStringAsync(limite.Token).ConfigureAwait(false);

            if (!resposta.IsSuccessStatusCode)
            {
                return new UploadOutcome(false, null, Explicar(resposta.StatusCode, corpo));
            }

            CloudLink? link = LerLink(corpo, filePath);

            if (link is null)
            {
                return new UploadOutcome(false, null, "O serviço respondeu de um jeito que não entendi.");
            }

            _log.Information(
                "Captura enviada para a nuvem: {Id}, expira em {Expira}",
                link.Id,
                link.ExpiresAt);

            return new UploadOutcome(true, link, "Link copiado. Expira em 48 horas.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new UploadOutcome(false, null, "O envio demorou demais e foi cancelado.");
        }
        catch (Exception excecao) when (excecao is HttpRequestException or IOException)
        {
            _log.Warning(excecao, "Falha ao enviar para a nuvem");
            return new UploadOutcome(false, null, "Não consegui falar com o serviço. Verifique a conexão.");
        }
    }

    /// <summary>
    /// Apaga antes da expiração.
    /// <para>
    /// O serviço trata "já não existe" como sucesso, e aqui é a mesma coisa: o
    /// resultado desejado é que a imagem não esteja mais lá.
    /// </para>
    /// </summary>
    public async Task<UploadOutcome> DeleteAsync(CloudLink link, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (!TryMontarUrl(_settings.Current.Cloud.Endpoint, link.Id, out Uri? destino))
        {
            return new UploadOutcome(false, null, "O endereço do serviço de nuvem não é uma URL válida.");
        }

        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limite.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            using var pedido = new HttpRequestMessage(HttpMethod.Delete, destino);
            pedido.Headers.TryAddWithoutValidation("x-token", link.Token);

            using HttpResponseMessage resposta = await _http.SendAsync(pedido, limite.Token).ConfigureAwait(false);

            if (resposta.IsSuccessStatusCode)
            {
                _log.Information("Imagem {Id} excluída da nuvem", link.Id);
                return new UploadOutcome(true, link, "A imagem saiu da nuvem.");
            }

            string corpo = await resposta.Content.ReadAsStringAsync(limite.Token).ConfigureAwait(false);
            return new UploadOutcome(false, link, Explicar(resposta.StatusCode, corpo));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new UploadOutcome(false, link, "A exclusão demorou demais.");
        }
        catch (Exception excecao) when (excecao is HttpRequestException or IOException)
        {
            _log.Warning(excecao, "Falha ao excluir da nuvem");
            return new UploadOutcome(false, link, "Não consegui falar com o serviço. Verifique a conexão.");
        }
    }

    private string? Chave()
    {
        string? configurada = _settings.Current.Cloud.Key;

        return string.IsNullOrWhiteSpace(configurada) ? ChaveDeCompilacao : configurada.Trim();
    }

    /// <summary>
    /// Monta o endereço a partir do que está configurado.
    /// <para>
    /// É o único lugar do programa que compõe URL da nuvem. Concatenar barra na mão em
    /// dois lugares diferentes é como um deles acaba com barra dupla meses depois.
    /// </para>
    /// </summary>
    private static bool TryMontarUrl(string? raiz, string segmento, out Uri? url)
    {
        url = null;

        if (string.IsNullOrWhiteSpace(raiz)
            || !Uri.TryCreate(raiz.TrimEnd('/') + '/' + segmento, UriKind.Absolute, out Uri? montada))
        {
            return false;
        }

        // Só HTTPS: o serviço recebe imagem de tela, que é conteúdo sensível por
        // natureza. Um endereço http:// no arquivo de configuração mandaria isso em
        // texto claro pela rede.
        if (montada.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        url = montada;
        return true;
    }

    private static string? TipoDe(string caminho) => Path.GetExtension(caminho).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        _ => null,
    };

    private static CloudLink? LerLink(string json, string localPath)
    {
        try
        {
            using JsonDocument documento = JsonDocument.Parse(json);
            JsonElement raiz = documento.RootElement;

            string? url = Texto(raiz, "url");
            string? id = Texto(raiz, "id");
            string? token = Texto(raiz, "token");

            if (url is null || id is null || token is null)
            {
                return null;
            }

            long expira = raiz.TryGetProperty("expiraEm", out JsonElement e) && e.TryGetInt64(out long n)
                ? n
                : DateTimeOffset.UtcNow.AddHours(48).ToUnixTimeSeconds();

            return new CloudLink(url, id, token, DateTimeOffset.FromUnixTimeSeconds(expira), localPath);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Transforma o código de resposta em frase.
    /// <para>
    /// O corpo do serviço é JSON, mas o 429 vem do limite de borda e chega como página
    /// HTML — por isso a mensagem do corpo é usada só quando dá para lê-la.
    /// </para>
    /// </summary>
    private static string Explicar(HttpStatusCode status, string corpo)
    {
        string? doServico = null;

        try
        {
            using JsonDocument documento = JsonDocument.Parse(corpo);
            doServico = Texto(documento.RootElement, "erro");
        }
        catch (JsonException)
        {
            // Resposta que não é JSON: cai nas frases por código, abaixo.
        }

        if (!string.IsNullOrWhiteSpace(doServico))
        {
            return doServico;
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => "O serviço recusou a chave deste programa.",
            HttpStatusCode.RequestEntityTooLarge => "A imagem passou do tamanho aceito pelo serviço.",
            HttpStatusCode.UnsupportedMediaType => "O serviço não aceita este formato de imagem.",
            (HttpStatusCode)429 => "Muitos envios seguidos. Espere alguns segundos e tente de novo.",
            HttpStatusCode.ServiceUnavailable => "O serviço atingiu o limite de envios do dia. Tente amanhã.",
            _ => $"O serviço respondeu {(int)status}.",
        };
    }

    private static string? Texto(JsonElement objeto, string propriedade)
        => objeto.TryGetProperty(propriedade, out JsonElement valor) && valor.ValueKind == JsonValueKind.String
            ? valor.GetString()
            : null;
}
