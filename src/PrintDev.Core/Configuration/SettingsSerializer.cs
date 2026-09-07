using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintDev.Core.Configuration;

/// <summary>
/// Converte <see cref="AppSettings"/> de e para JSON.
/// <para>
/// As opções aqui existem para tolerar arquivo editado à mão: comentário, vírgula
/// sobrando e diferença de maiúsculas não podem fazer o programa recusar as
/// configurações de quem abriu o arquivo para mexer.
/// </para>
/// </summary>
public static class SettingsSerializer
{
    /// <summary>Opções usadas na leitura e na escrita.</summary>
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Sem escape agressivo: o arquivo tem texto em portugues e caminho do Windows,
        // e precisa continuar legivel para quem abrir no editor.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            // Enum como texto em camelCase: "png" le melhor que 0, e sobrevive a
            // reordenacao dos membros do enum, que um numero nao sobrevive.
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true),
        },
    };

    /// <summary>Serializa as configurações.</summary>
    public static string Serialize(AppSettings settings)
        => JsonSerializer.Serialize(settings, Options);

    /// <summary>
    /// Desserializa as configurações.
    /// </summary>
    /// <exception cref="JsonException">Quando o conteúdo não é JSON válido.</exception>
    public static AppSettings Deserialize(string json)
        => JsonSerializer.Deserialize<AppSettings>(json, Options)
           ?? throw new JsonException("O arquivo de configurações está vazio ou é nulo.");
}
