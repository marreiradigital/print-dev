using System.IO;
using System.Text.Json;
using PrintDev.Core.Infrastructure;
using Serilog;

namespace PrintDev.Core.Configuration;

/// <summary>
/// Implementação de <see cref="ISettingsService"/> sobre um arquivo JSON.
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    /// <summary>
    /// Espera antes de gravar. As configurações valem na hora; só o disco espera.
    /// Arrastar um controle deslizante dispara dezenas de mudanças por segundo e não
    /// pode virar dezenas de gravações.
    /// </summary>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Espera antes de reler quando o arquivo muda por fora. Um editor de texto dispara
    /// vários eventos por gravação; o agendamento junta todos numa releitura só.
    /// </summary>
    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(300);

    private readonly IAppPaths _paths;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Timer _saveTimer;
    private readonly Timer _reloadTimer;

    private FileSystemWatcher? _watcher;
    private AppSettings _current = new();
    private bool _savePending;
    private bool _disposed;

    public JsonSettingsService(IAppPaths paths, ILogger log)
    {
        _paths = paths;
        _log = log.ForContext<JsonSettingsService>();

        _saveTimer = new Timer(_ => SaveNow(), state: null, Timeout.Infinite, Timeout.Infinite);
        _reloadTimer = new Timer(_ => ReloadFromDisk(), state: null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc/>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<AppSettings>? Changed;

    /// <inheritdoc/>
    public void Load()
    {
        if (TryLoadFromDisk(out AppSettings loaded, out bool shouldRewrite))
        {
            lock (_gate)
            {
                _current = loaded;
            }

            if (shouldRewrite)
            {
                SaveNow();
            }
        }

        StartWatching();
    }

    /// <inheritdoc/>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AppSettings next;
        lock (_gate)
        {
            next = change(_current) ?? throw new InvalidOperationException(
                "A função de mudança devolveu nulo.");

            // Igualdade estrutural de record: mudanca que nao muda nada nao gera
            // gravacao nem evento.
            if (next == _current)
            {
                return;
            }

            _current = next;
            _savePending = true;
        }

        _saveTimer.Change(SaveDebounce, Timeout.InfiniteTimeSpan);
        Changed?.Invoke(this, next);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveNow();
    }

    /// <summary>
    /// Lê o arquivo do disco.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> quando o arquivo existe mas não deu para ler agora
    /// (bloqueado por outro processo, por exemplo). Nesse caso quem chama mantém o que
    /// já tem em memória — nunca volta aos padrões por causa de uma falha passageira.
    /// </returns>
    private bool TryLoadFromDisk(out AppSettings settings, out bool shouldRewrite)
    {
        settings = new AppSettings();
        shouldRewrite = false;
        string path = _paths.SettingsFile;

        if (!File.Exists(path))
        {
            // Primeira execucao: grava o arquivo com os padroes para o usuario ter o
            // que abrir e editar, em vez de um caminho que nao existe.
            _log.Information("Sem arquivo de configurações; criando com os padrões em {Caminho}", path);
            shouldRewrite = true;
            return true;
        }

        string? json = TryReadAllText(path);
        if (json is null)
        {
            _log.Warning("Não consegui ler {Caminho} agora; mantendo as configurações em memória.", path);
            return false;
        }

        try
        {
            AppSettings parsed = SettingsSerializer.Deserialize(json);
            settings = SettingsMigrator.Migrate(parsed, out bool migrated);
            shouldRewrite = migrated;
            _log.Debug("Configurações lidas de {Caminho}", path);
            return true;
        }
        catch (JsonException exception)
        {
            // Conteudo invalido e diferente de falha de leitura: aqui o arquivo esta
            // acessivel e o que tem dentro nao presta.
            settings = Quarantine(path, exception);
            shouldRewrite = true;
            return true;
        }
    }

    /// <summary>
    /// Lê o arquivo com algumas tentativas.
    /// <para>
    /// A gravação é atômica, então o arquivo nunca está pela metade — mas durante a
    /// troca ele pode estar momentaneamente bloqueado, e um editor de texto costuma
    /// segurá-lo por instantes ao salvar. Desistir na primeira tentativa faria uma
    /// disputa de milissegundos parecer corrupção.
    /// </para>
    /// </summary>
    private static string? TryReadAllText(string path)
    {
        const int attempts = 4;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == attempts - 1)
                {
                    return null;
                }

                Thread.Sleep(60);
            }
        }

        return null;
    }

    /// <summary>
    /// Põe de lado um arquivo que não deu para interpretar e volta aos padrões. O
    /// arquivo original é preservado: pode ter horas de ajuste do usuário, e um erro de
    /// digitação não é motivo para jogar tudo fora.
    /// </summary>
    private AppSettings Quarantine(string path, Exception exception)
    {
        string quarantined = Path.Combine(
            Path.GetDirectoryName(path)!,
            $"settings.corrompido-{DateTime.Now:yyyyMMdd-HHmmss}.json");

        try
        {
            File.Move(path, quarantined, overwrite: true);
            _log.Error(
                exception,
                "Não consegui ler as configurações. Guardei o arquivo em {Guardado} e voltei aos padrões.",
                quarantined);
        }
        catch (Exception moveFailure)
        {
            _log.Error(moveFailure, "Não consegui nem guardar o arquivo de configurações inválido.");
        }

        return new AppSettings();
    }

    private void SaveNow()
    {
        AppSettings snapshot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            snapshot = _current;
            _savePending = false;
        }

        try
        {
            AtomicFileWriter.WriteAllText(_paths.SettingsFile, SettingsSerializer.Serialize(snapshot));
            _log.Debug("Configurações gravadas em {Caminho}", _paths.SettingsFile);
        }
        catch (Exception exception)
        {
            // Nao poder gravar a preferencia nao pode derrubar o programa: o usuario
            // perde a configuracao entre sessoes, nao a captura que acabou de fazer.
            _log.Error(exception, "Falha ao gravar as configurações em {Caminho}", _paths.SettingsFile);
        }
    }

    private void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_paths.SettingsDirectory, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;
        }
        catch (Exception exception)
        {
            // Observar o arquivo e conveniencia. Se o sistema recusar, o programa
            // continua funcionando; so nao recarrega sozinho.
            _log.Warning(exception, "Não consegui observar o arquivo de configurações.");
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
        => _reloadTimer.Change(ReloadDebounce, Timeout.InfiniteTimeSpan);

    /// <summary>
    /// Relê o arquivo depois de uma mudança observada.
    /// <para>
    /// A nossa própria gravação também dispara o observador, e é por isso que a
    /// comparação abaixo é por CONTEÚDO e não por tempo: uma janela de tempo para
    /// ignorar a própria escrita engoliria junto a edição legítima que o usuário fizesse
    /// logo depois. Comparando o resultado, a própria gravação simplesmente não gera
    /// evento, e qualquer diferença real gera.
    /// </para>
    /// </summary>
    private void ReloadFromDisk()
    {
        if (!TryLoadFromDisk(out AppSettings loaded, out _))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || loaded == _current)
            {
                return;
            }

            _current = loaded;
        }

        _log.Information("Configurações recarregadas do arquivo");
        Changed?.Invoke(this, loaded);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        bool needsFinalSave;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            needsFinalSave = _savePending;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileTouched;
            _watcher.Created -= OnFileTouched;
            _watcher.Renamed -= OnFileTouched;
            _watcher.Dispose();
            _watcher = null;
        }

        // Grava o que ficou pendente ANTES de marcar como descartado: encerrar o
        // programa nao pode perder o ajuste que o usuario acabou de fazer.
        if (needsFinalSave)
        {
            SaveNow();
        }

        lock (_gate)
        {
            _disposed = true;
        }

        _saveTimer.Dispose();
        _reloadTimer.Dispose();
    }
}
