using System.IO;
using System.Text.Json;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;
using Serilog;

namespace PrintDev.Core.Updates;

/// <summary>
/// Guarda o que a verificação de atualização precisa lembrar entre execuções.
/// <para>
/// Fica fora do <c>settings.json</c> de propósito. Aquele arquivo é feito para o
/// usuário editar à mão; isto aqui é estado de máquina — validador de cache, carimbo
/// de hora, caminho de arquivo temporário — que só polui a leitura e que ninguém
/// deveria querer ajustar.
/// </para>
/// </summary>
public sealed class UpdateStateStore
{
    private const string FileName = "atualizacao.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _file;
    private readonly ILogger _log;

    public UpdateStateStore(IAppPaths paths, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _file = Path.Combine(paths.SettingsDirectory, FileName);
        _log = log.ForContext<UpdateStateStore>();
    }

    /// <summary>
    /// Lê o estado. Arquivo ausente, vazio ou corrompido devolve o estado zerado — a
    /// consequência é uma consulta a mais ao GitHub, e não um programa que não abre.
    /// </summary>
    public UpdateState Read()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return new UpdateState();
            }

            string json = File.ReadAllText(_file);

            return string.IsNullOrWhiteSpace(json)
                ? new UpdateState()
                : JsonSerializer.Deserialize<UpdateState>(json, Options) ?? new UpdateState();
        }
        catch (Exception excecao) when (excecao is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.Debug(excecao, "Estado de atualização ilegível; recomeçando do zero");
            return new UpdateState();
        }
    }

    /// <summary>Grava o estado. Falha ao gravar nunca derruba nada: é cache.</summary>
    public void Write(UpdateState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        try
        {
            AtomicFileWriter.WriteAllText(_file, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception excecao) when (excecao is IOException or UnauthorizedAccessException)
        {
            _log.Debug(excecao, "Não consegui gravar o estado de atualização");
        }
    }
}
