using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;
using Serilog;

namespace PrintDev.Core.Cloud;

/// <summary>
/// Lembra o que já foi enviado, para dar conta de excluir antes da expiração.
/// <para>
/// Sem isto o botão "Excluir agora" seria impossível: o serviço guarda apenas o
/// <b>digesto</b> do token de exclusão, então o token em claro só existe aqui. Perder
/// este arquivo não expõe nada — apenas obriga a esperar as 48 horas.
/// </para>
/// <para>
/// Guarda segredo, e por isso mora ao lado do arquivo de configurações, na pasta do
/// perfil do usuário. Não é lugar de sigilo forte, e a consequência de vazá-lo é
/// alguém poder apagar uma imagem que já ia se apagar sozinha.
/// </para>
/// </summary>
public sealed class CloudLinkStore
{
    private const string FileName = "nuvem.json";

    /// <summary>
    /// Teto de entradas guardadas. Com 48 horas de validade, passar disso significa
    /// que as mais antigas já expiraram de qualquer jeito.
    /// </summary>
    private const int MaxEntries = 100;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _file;
    private readonly ILogger _log;
    // System.Threading.Lock só existe do .NET 9 em diante; aqui o alvo é o 8.
    private readonly object _trava = new();
    private List<Entrada>? _cache;

    public CloudLinkStore(IAppPaths paths, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _file = Path.Combine(paths.SettingsDirectory, FileName);
        _log = log.ForContext<CloudLinkStore>();
    }

    /// <summary>Envios ainda válidos, do mais recente para o mais antigo.</summary>
    public IReadOnlyList<CloudLink> Items
    {
        get
        {
            lock (_trava)
            {
                return Carregar()
                    .Where(NaoExpirou)
                    .OrderByDescending(e => e.ExpiraEm)
                    .Select(e => e.ParaLink())
                    .ToList();
            }
        }
    }

    /// <summary>O envio ainda válido desta captura, se houver.</summary>
    public CloudLink? ParaArquivo(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        lock (_trava)
        {
            return Carregar()
                .Where(NaoExpirou)
                .FirstOrDefault(e => string.Equals(e.Arquivo, localPath, StringComparison.OrdinalIgnoreCase))
                ?.ParaLink();
        }
    }

    /// <summary>Registra um envio.</summary>
    public void Add(CloudLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        lock (_trava)
        {
            List<Entrada> entradas = Carregar();

            entradas.RemoveAll(e => e.Id == link.Id);
            entradas.Add(Entrada.De(link));

            Gravar(entradas);
        }
    }

    /// <summary>Esquece um envio — depois de excluí-lo, ou quando ele expira.</summary>
    public void Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        lock (_trava)
        {
            List<Entrada> entradas = Carregar();

            if (entradas.RemoveAll(e => e.Id == id) > 0)
            {
                Gravar(entradas);
            }
        }
    }

    private static bool NaoExpirou(Entrada entrada)
        => DateTimeOffset.FromUnixTimeSeconds(entrada.ExpiraEm) > DateTimeOffset.UtcNow;

    private List<Entrada> Carregar()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (File.Exists(_file))
            {
                string json = File.ReadAllText(_file);

                _cache = string.IsNullOrWhiteSpace(json)
                    ? []
                    : JsonSerializer.Deserialize<List<Entrada>>(json, Options) ?? [];
            }
            else
            {
                _cache = [];
            }
        }
        catch (Exception excecao) when (excecao is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Debug(excecao, "Registro de envios ilegível; recomeçando vazio");
            _cache = [];
        }

        return _cache;
    }

    private void Gravar(List<Entrada> entradas)
    {
        // A poda acontece na gravação, e não na leitura: é o único momento em que já se
        // está pagando o custo de tocar o disco.
        List<Entrada> vivas = entradas
            .Where(NaoExpirou)
            .OrderByDescending(e => e.ExpiraEm)
            .Take(MaxEntries)
            .ToList();

        _cache = vivas;

        try
        {
            AtomicFileWriter.WriteAllText(_file, JsonSerializer.Serialize(vivas, Options));
        }
        catch (Exception excecao) when (excecao is IOException or UnauthorizedAccessException)
        {
            _log.Warning(excecao, "Não consegui gravar o registro de envios");
        }
    }

    /// <summary>Forma gravada em disco. Instante em segundos, para não depender de fuso.</summary>
    private sealed record Entrada
    {
        [JsonPropertyName("url")]
        public string Url { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;

        [JsonPropertyName("expiraEm")]
        public long ExpiraEm { get; init; }

        [JsonPropertyName("arquivo")]
        public string Arquivo { get; init; } = string.Empty;

        public static Entrada De(CloudLink link) => new()
        {
            Url = link.Url,
            Id = link.Id,
            Token = link.Token,
            ExpiraEm = link.ExpiresAt.ToUnixTimeSeconds(),
            Arquivo = link.LocalPath,
        };

        public CloudLink ParaLink() => new(
            Url,
            Id,
            Token,
            DateTimeOffset.FromUnixTimeSeconds(ExpiraEm),
            Arquivo);
    }
}
