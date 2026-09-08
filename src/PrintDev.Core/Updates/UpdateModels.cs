using System.Text.Json.Serialization;

namespace PrintDev.Core.Updates;

/// <summary>Uma versão publicada mais nova que a instalada.</summary>
/// <param name="Version">Versão com três componentes.</param>
/// <param name="Tag">A etiqueta original, para mostrar e para guardar em "ignorada".</param>
/// <param name="Notes">Notas do lançamento, em Markdown, como o autor escreveu.</param>
/// <param name="DownloadUrl">Endereço direto do instalador.</param>
/// <param name="AssetName">Nome do arquivo, usado no disco e na mensagem ao usuário.</param>
/// <param name="SizeBytes">Tamanho anunciado, para a barra de progresso e a confirmação.</param>
/// <param name="Sha256">
/// Digesto que a própria API do GitHub publica para o arquivo. É o que permite conferir
/// a integridade do download sem eu manter um arquivo de somas à parte — e ele chega
/// por uma conexão TLS com api.github.com, não pelo mesmo canal do arquivo.
/// </param>
public sealed record AvailableUpdate(
    Version Version,
    string Tag,
    string Notes,
    string DownloadUrl,
    string AssetName,
    long SizeBytes,
    string? Sha256);

/// <summary>Como terminou uma consulta por atualização.</summary>
public enum UpdateStatus
{
    /// <summary>Ainda não se olhou desde que o programa subiu.</summary>
    NaoVerificado,

    /// <summary>Consultado, e esta é a versão mais nova que existe.</summary>
    Atualizado,

    /// <summary>Existe versão mais nova.</summary>
    Disponivel,

    /// <summary>Já baixado e conferido, esperando a hora de instalar.</summary>
    Baixado,

    /// <summary>Não deu para verificar. A frase explica.</summary>
    Falhou,
}

/// <summary>O que a consulta encontrou, com uma frase pronta para a interface.</summary>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    AvailableUpdate? Update,
    string Message)
{
    public static UpdateCheckResult Atualizado(Version atual)
        => new(UpdateStatus.Atualizado, null, $"Você está na versão mais recente ({atual.ToString(3)}).");

    public static UpdateCheckResult Falha(string motivo)
        => new(UpdateStatus.Falhou, null, motivo);
}

/// <summary>
/// O que sobrevive entre execuções: o suficiente para não perguntar ao GitHub de novo
/// antes da hora, e para não reoferecer uma versão que o usuário já dispensou.
/// </summary>
public sealed record UpdateState
{
    /// <summary>
    /// Validador da última resposta. Reenviado como <c>If-None-Match</c>: quando nada
    /// mudou, o GitHub responde 304 sem corpo — e resposta condicional com 304 não
    /// consome a cota de requisições.
    /// </summary>
    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    [JsonPropertyName("ultimaVerificacao")]
    public DateTimeOffset? LastCheck { get; init; }

    /// <summary>A etiqueta mais nova já vista, mesmo que o programa tenha sido fechado.</summary>
    [JsonPropertyName("versaoEncontrada")]
    public string? FoundTag { get; init; }

    /// <summary>
    /// Etiqueta que o usuário mandou pular. Só esta versão é silenciada; a próxima
    /// volta a avisar, porque "não quero a 0.2.0" não é "nunca mais me avise".
    /// </summary>
    [JsonPropertyName("versaoIgnorada")]
    public string? IgnoredTag { get; init; }

    /// <summary>Instalador já baixado e com o digesto conferido, à espera da instalação.</summary>
    [JsonPropertyName("instaladorPronto")]
    public string? ReadyInstaller { get; init; }

    /// <summary>A que etiqueta o instalador pronto corresponde.</summary>
    [JsonPropertyName("instaladorDaVersao")]
    public string? ReadyInstallerTag { get; init; }

    /// <summary>
    /// Última etiqueta que o programa tentou instalar de fato.
    /// <para>
    /// Existe para fechar um buraco: a instalação silenciosa acontece <b>depois</b> de
    /// o programa encerrar, então ninguém está vivo para ver o código de saída. Se na
    /// partida seguinte a versão em execução ainda for menor que esta, a instalação
    /// falhou em silêncio — e aí o programa avisa em vez de tentar de novo para sempre.
    /// </para>
    /// </summary>
    [JsonPropertyName("ultimaTentativaDaVersao")]
    public string? LastAttemptTag { get; init; }

    [JsonPropertyName("ultimaTentativaEm")]
    public DateTimeOffset? LastAttemptAt { get; init; }
}
