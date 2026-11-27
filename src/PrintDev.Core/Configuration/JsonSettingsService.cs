using System.IO;
using System.Text.Json;
using PrintDev.Core.Infrastructure;
using Serilog;

namespace PrintDev.Core.Configuration;

/// <summary>
/// Implementação de <see cref="ISettingsService"/> sobre um arquivo JSON.
/// <para>
/// Há três atores mexendo no mesmo estado: a interface (<see cref="Update"/>), o
/// temporizador de gravação e o observador do arquivo. As regras que mantêm os três
/// em paz, e que os testes travam:
/// </para>
/// <list type="bullet">
/// <item>Uma mudança em memória que ainda não foi para o disco <b>nunca</b> é desfeita
/// por uma releitura: a releitura espera a gravação.</item>
/// <item>A nossa própria gravação dispara o observador; o eco é reconhecido pelo
/// conteúdo (o texto no disco é exatamente o último que escrevemos) e ignorado.</item>
/// <item>Gravação que falha continua pendente e é refeita, em vez de ser dada como
/// feita.</item>
/// <item>Arquivo que existe mas não deu para ler <b>nunca</b> é sobrescrito com
/// padrões: as mudanças feitas enquanto isso ficam na memória e são reaplicadas por
/// cima do conteúdo real quando ele puder ser lido.</item>
/// <item>O aviso <see cref="Changed"/> sai sempre na ordem das mudanças.</item>
/// </list>
/// </summary>
public sealed class JsonSettingsService : ISettingsService
{
    /// <summary>Quantas mudanças guardar para reaplicar sobre um arquivo que não deu para ler.</summary>
    private const int MaxReplay = 500;

    private readonly IAppPaths _paths;
    private readonly ILogger _log;
    private readonly Timings _timings;

    // Protege o estado abaixo. Cadeado curto: nada de E/S de disco enquanto ele esta
    // seguro (a unica excecao e a funcao de mudanca do Update, que e pura).
    private readonly object _gate = new();

    // Uma gravacao por vez, e sempre a partir do estado MAIS NOVO. Sem isto, dois
    // gravadores (o temporizador e o Flush) podiam terminar fora de ordem e deixar no
    // disco o instantaneo velho por cima do novo.
    private readonly object _writeGate = new();

    // Uma releitura por vez: o temporizador pode disparar de novo enquanto a anterior
    // ainda esta lendo.
    private readonly object _reloadGate = new();

    private readonly Timer _saveTimer;
    private readonly Timer _reloadTimer;

    // Avisos a entregar, na ordem em que as mudancas aconteceram.
    private readonly Queue<AppSettings> _outbox = new();

    // Mudancas feitas enquanto o arquivo estava ilegivel, para reaplicar por cima dele.
    private readonly List<Func<AppSettings, AppSettings>> _replay = [];

    private FileSystemWatcher? _watcher;
    private AppSettings _current = new();

    // Sobe a cada mudanca do estado em memoria. Quem le o disco guarda o numero de
    // antes e confere depois: se mudou, o que leu e velho demais para valer.
    private long _version;

    // O texto que sabemos estar no disco: o que escrevemos por ultimo ou o que lemos
    // por ultimo. E com ele que o eco da nossa propria gravacao e reconhecido.
    private string? _lastDiskText;

    private bool _savePending;
    private bool _diskUnreadable;
    private bool _delivering;
    private int _saveFailures;
    private bool _disposed;

    public JsonSettingsService(IAppPaths paths, ILogger log)
        : this(paths, log, Timings.Default)
    {
    }

    internal JsonSettingsService(IAppPaths paths, ILogger log, Timings timings)
    {
        _paths = paths;
        _log = log.ForContext<JsonSettingsService>();
        _timings = timings;

        _saveTimer = new Timer(_ => OnSaveTimer(), state: null, Timeout.Infinite, Timeout.Infinite);
        _reloadTimer = new Timer(_ => OnReloadTimer(), state: null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Os tempos do serviço. Os testes encurtam; o aplicativo usa os padrões.</summary>
    internal sealed record Timings
    {
        public static Timings Default { get; } = new();

        /// <summary>
        /// Espera antes de gravar. As configurações valem na hora; só o disco espera.
        /// Arrastar um controle deslizante dispara dezenas de mudanças por segundo e não
        /// pode virar dezenas de gravações.
        /// </summary>
        public TimeSpan SaveDebounce { get; init; } = TimeSpan.FromMilliseconds(500);

        /// <summary>De quanto em quanto tempo tentar gravar de novo depois de uma falha.</summary>
        public TimeSpan SaveRetry { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Espera antes de reler quando o arquivo muda por fora. Um editor de texto dispara
        /// vários eventos por gravação; o agendamento junta todos numa releitura só.
        /// </summary>
        public TimeSpan ReloadDebounce { get; init; } = TimeSpan.FromMilliseconds(300);

        /// <summary>De quanto em quanto tempo tentar ler de novo um arquivo que estava bloqueado.</summary>
        public TimeSpan UnreadableRetry { get; init; } = TimeSpan.FromSeconds(3);

        /// <summary>
        /// Tentativas de leitura na partida. Antivírus e editores seguram o arquivo por
        /// instantes; desistir na primeira faria uma disputa de milissegundos parecer um
        /// arquivo ausente.
        /// </summary>
        public int LoadAttempts { get; init; } = 10;

        /// <summary>Tentativas de leitura numa releitura (o arquivo acabou de mudar).</summary>
        public int ReloadAttempts { get; init; } = 4;

        /// <summary>Espera entre uma tentativa de leitura e a seguinte.</summary>
        public TimeSpan ReadAttemptDelay { get; init; } = TimeSpan.FromMilliseconds(80);
    }

    private enum DiskState
    {
        Missing,
        Unreadable,
        Readable,
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
        string path = _paths.SettingsFile;
        DiskState state = ReadDisk(out string? text, _timings.LoadAttempts);

        switch (state)
        {
            case DiskState.Missing:
                // Primeira execucao: grava o arquivo com os padroes para o usuario ter o
                // que abrir e editar, em vez de um caminho que nao existe.
                _log.Information("Sem arquivo de configurações; criando com os padrões em {Caminho}", path);

                lock (_gate)
                {
                    _current = new AppSettings();
                    _version++;
                    _lastDiskText = null;
                    _savePending = true;
                }

                SaveCore();
                break;

            case DiskState.Unreadable:
                // O arquivo existe e tem o que o usuario configurou, mas esta bloqueado.
                // Subir com os padroes e deixar a primeira mudanca gravar por cima
                // apagaria tudo; entao o programa funciona com os padroes na memoria,
                // NAO grava, e tenta ler de novo ate conseguir.
                _log.Warning(
                    "Não consegui ler {Caminho} (bloqueado por outro programa?). Sigo com os padrões na memória, sem gravar por cima do arquivo, e tento de novo.",
                    path);

                lock (_gate)
                {
                    _diskUnreadable = true;
                }

                ScheduleReload(_timings.UnreadableRetry);
                break;

            default:
                ApplyLoadedAtStartup(path, text!);
                break;
        }

        StartWatching();
    }

    private void ApplyLoadedAtStartup(string path, string text)
    {
        bool valid = TryParse(text, out AppSettings loaded, out bool migrated, out JsonException? error);

        if (!valid)
        {
            // Conteudo invalido e diferente de falha de leitura: aqui o arquivo esta
            // acessivel e o que tem dentro nao presta.
            loaded = Quarantine(path, error!);
        }

        bool rewrite = migrated || !valid;

        lock (_gate)
        {
            _current = loaded;
            _version++;

            // Posto de lado, o arquivo deixou de existir; senao o disco ainda tem este texto.
            _lastDiskText = valid ? text : null;

            if (rewrite)
            {
                _savePending = true;
            }
        }

        _log.Debug("Configurações lidas de {Caminho}", path);

        if (rewrite)
        {
            SaveCore();
        }
    }

    /// <inheritdoc/>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            AppSettings next = change(_current) ?? throw new InvalidOperationException(
                "A função de mudança devolveu nulo.");

            // Igualdade estrutural de record: mudanca que nao muda nada nao gera
            // gravacao nem evento.
            if (next == _current)
            {
                return;
            }

            _current = next;
            _version++;
            _savePending = true;

            if (_diskUnreadable)
            {
                if (_replay.Count >= MaxReplay)
                {
                    _replay.RemoveAt(0);
                }

                _replay.Add(change);
            }

            // Na fila dentro do mesmo cadeado da mudanca: a ordem de entrega e a ordem
            // em que as mudancas aconteceram, mesmo com duas threads mudando.
            _outbox.Enqueue(next);
        }

        ArmSave(_timings.SaveDebounce);
        Deliver();
    }

    /// <inheritdoc/>
    public void Flush()
    {
        try
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }

        SaveCore();
    }

    /// <summary>
    /// Interpreta o texto lido. Não mexe em disco: quem decide o que fazer com um
    /// conteúdo inválido é quem chama.
    /// </summary>
    private static bool TryParse(string json, out AppSettings settings, out bool migrated, out JsonException? error)
    {
        try
        {
            AppSettings parsed = SettingsSerializer.Deserialize(json);
            settings = SettingsMigrator.Migrate(parsed, out migrated);
            error = null;
            return true;
        }
        catch (JsonException exception)
        {
            settings = new AppSettings();
            migrated = false;
            error = exception;
            return false;
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
    /// <para>
    /// <see cref="DiskState.Unreadable"/> é "existe, mas não deu para ler agora": quem
    /// chama mantém o que tem em memória e nunca volta aos padrões por causa de uma
    /// falha passageira.
    /// </para>
    /// </summary>
    private DiskState ReadDisk(out string? text, int attempts)
    {
        text = null;
        string path = _paths.SettingsFile;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                text = File.ReadAllText(path);
                return DiskState.Readable;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return DiskState.Missing;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts)
                {
                    return DiskState.Unreadable;
                }

                Thread.Sleep(_timings.ReadAttemptDelay);
            }
        }
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

    private void OnSaveTimer()
    {
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            SaveCore();
        }
        catch (Exception exception)
        {
            // Excecao solta numa thread do pool derruba o processo inteiro.
            _log.Error(exception, "Falha inesperada ao gravar as configurações");
        }
    }

    /// <summary>
    /// Grava o que estiver pendente.
    /// <para>
    /// A marca de "pendente" só cai DEPOIS de a gravação dar certo, e só se nada mudou
    /// no meio do caminho. Limpar antes de gravar (como era) fazia uma falha — o arquivo
    /// aberto em outro programa, o disco cheio — virar mudança perdida em silêncio: o
    /// programa achava que estava tudo no disco.
    /// </para>
    /// </summary>
    private void SaveCore()
    {
        lock (_writeGate)
        {
            AppSettings snapshot;
            long version;

            lock (_gate)
            {
                if (!_savePending)
                {
                    return;
                }

                if (_diskUnreadable)
                {
                    // Nunca por cima de um arquivo que nao deu para ler; a releitura
                    // reaplica as mudancas e reabre a gravacao.
                    _log.Debug("Gravação adiada: o arquivo de configurações ainda não pôde ser lido.");
                    return;
                }

                snapshot = _current;
                version = _version;
            }

            try
            {
                string text = SettingsSerializer.Serialize(snapshot);
                AtomicFileWriter.WriteAllText(_paths.SettingsFile, text);

                bool recovered;
                lock (_gate)
                {
                    _lastDiskText = text;

                    // Se mudou durante a gravacao, o que foi para o disco ja nasceu velho:
                    // continua pendente, e o Update que mudou ja armou a proxima gravacao.
                    if (_version == version)
                    {
                        _savePending = false;
                    }

                    recovered = _saveFailures > 0;
                    _saveFailures = 0;
                }

                if (recovered)
                {
                    _log.Information("Configurações gravadas em {Caminho} (a gravação voltou a funcionar)", _paths.SettingsFile);
                }
                else
                {
                    _log.Debug("Configurações gravadas em {Caminho}", _paths.SettingsFile);
                }
            }
            catch (Exception exception)
            {
                // Nao poder gravar a preferencia nao pode derrubar o programa: o usuario
                // perde a configuracao entre sessoes, nao a captura que acabou de fazer.
                // Mas tambem nao pode ser esquecido: continua pendente e tenta de novo.
                int failures;
                bool disposed;

                lock (_gate)
                {
                    failures = ++_saveFailures;
                    disposed = _disposed;
                }

                if (failures == 1)
                {
                    _log.Error(
                        exception,
                        "Falha ao gravar as configurações em {Caminho}. Vou tentar de novo a cada {Segundos:0} s.",
                        _paths.SettingsFile,
                        _timings.SaveRetry.TotalSeconds);
                }
                else
                {
                    _log.Debug(exception, "Nova falha ao gravar as configurações (tentativa {Tentativa})", failures);
                }

                if (!disposed)
                {
                    ArmSave(_timings.SaveRetry);
                }
            }
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
        => ScheduleReload(_timings.ReloadDebounce);

    private void OnReloadTimer()
    {
        try
        {
            ReloadFromDisk();
        }
        catch (Exception exception)
        {
            // Excecao solta numa thread do pool derruba o processo inteiro.
            _log.Error(exception, "Falha inesperada ao reler as configurações");
        }
    }

    /// <summary>
    /// Relê o arquivo depois de uma mudança observada.
    /// <para>
    /// A nossa própria gravação também dispara o observador, e é por isso que o eco é
    /// reconhecido por CONTEÚDO e não por tempo: uma janela de tempo para ignorar a
    /// própria escrita engoliria junto a edição legítima que o usuário fizesse logo
    /// depois. Se o texto no disco é exatamente o último que escrevemos, não há notícia.
    /// </para>
    /// <para>
    /// E uma releitura nunca vale mais que uma mudança ainda não gravada: o disco só
    /// "está atrás" da memória, não "está certo". Reverter para ele desfazia o ajuste
    /// que o usuário acabara de fazer.
    /// </para>
    /// </summary>
    internal void ReloadFromDisk()
    {
        lock (_reloadGate)
        {
            long seen;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                seen = _version;
            }

            DiskState state = ReadDisk(out string? text, _timings.ReloadAttempts);

            if (state == DiskState.Unreadable)
            {
                // Mantem o que tem. Se o arquivo nunca chegou a ser lido, insiste.
                if (IsDiskUnreadable())
                {
                    ScheduleReload(_timings.UnreadableRetry);
                }
                else
                {
                    _log.Warning("Não consegui reler as configurações agora; mantenho as que estão em uso.");
                }

                return;
            }

            if (state == DiskState.Missing)
            {
                // Arquivo sumido (ou editor que troca por renomeacao, num instante) nao
                // e pedido para voltar aos padroes: a memoria manda, e a proxima gravacao
                // recria o arquivo. Exceto se ele nunca chegou a ser lido: ai a memoria
                // e a unica fonte, e vale gravar.
                lock (_gate)
                {
                    if (_diskUnreadable && _version == seen)
                    {
                        _diskUnreadable = false;
                        _replay.Clear();
                        _lastDiskText = null;
                        _savePending = true;
                        ArmSave(_timings.SaveDebounce);
                    }
                }

                return;
            }

            lock (_gate)
            {
                if (!_diskUnreadable && string.Equals(text, _lastDiskText, StringComparison.Ordinal))
                {
                    // Eco da nossa gravacao (ou de um toque que nao mudou nada).
                    return;
                }
            }

            bool valid = TryParse(text!, out AppSettings loaded, out bool _, out JsonException? error);
            bool recovering;
            int replayed = 0;

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                recovering = _diskUnreadable;

                if (_version != seen)
                {
                    // Mudou enquanto lia: o que foi lido e velho. Se ainda estamos tentando
                    // recuperar o arquivo, olha de novo logo; senao, a gravacao daquela
                    // mudanca gera o proprio eco e fecha o assunto.
                    if (recovering)
                    {
                        ScheduleReload(_timings.ReloadDebounce);
                    }

                    return;
                }

                if (_savePending && !recovering)
                {
                    _log.Information(
                        "O arquivo de configurações mudou por fora, mas há uma mudança feita no programa ainda não gravada; ela prevalece.");
                    return;
                }

                if (recovering)
                {
                    foreach (Func<AppSettings, AppSettings> change in _replay)
                    {
                        try
                        {
                            loaded = change(loaded);
                            replayed++;
                        }
                        catch (Exception exception)
                        {
                            _log.Warning(exception, "Não consegui reaplicar uma mudança feita enquanto o arquivo estava bloqueado.");
                        }
                    }

                    _replay.Clear();
                    _diskUnreadable = false;
                }

                _current = loaded;
                _version++;
                _lastDiskText = valid ? text : null;

                if (replayed > 0)
                {
                    _savePending = true;
                }

                _outbox.Enqueue(loaded);
            }

            if (!valid)
            {
                Quarantine(_paths.SettingsFile, error!);
            }

            if (replayed > 0)
            {
                ArmSave(_timings.SaveDebounce);
            }

            if (recovering)
            {
                _log.Information(
                    "Configurações lidas do arquivo, que estava bloqueado; {Reaplicadas} mudança(s) feitas nesse meio tempo foram mantidas",
                    replayed);
            }
            else
            {
                _log.Information("Configurações recarregadas do arquivo");
            }

            Deliver();
        }
    }

    private bool IsDiskUnreadable()
    {
        lock (_gate)
        {
            return _diskUnreadable;
        }
    }

    /// <summary>
    /// Entrega os avisos pendentes, um de cada vez e na ordem.
    /// <para>
    /// Só uma thread entrega por vez: quem chega enquanto outra entrega apenas deixa o
    /// aviso na fila e volta. Assim dois avisos nunca chegam trocados, e uma assinatura
    /// que muda a configuração de dentro do aviso não vira recursão.
    /// </para>
    /// </summary>
    private void Deliver()
    {
        lock (_gate)
        {
            if (_delivering)
            {
                return;
            }

            _delivering = true;
        }

        while (true)
        {
            AppSettings next;

            lock (_gate)
            {
                if (_outbox.Count == 0)
                {
                    _delivering = false;
                    return;
                }

                next = _outbox.Dequeue();
            }

            EventHandler<AppSettings>? handlers = Changed;
            if (handlers is null)
            {
                continue;
            }

            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try
                {
                    ((EventHandler<AppSettings>)handler)(this, next);
                }
                catch (Exception exception)
                {
                    // Um assinante com defeito nao pode impedir os outros nem estragar a fila.
                    _log.Error(exception, "Falha num assinante do aviso de mudança das configurações");
                }
            }
        }
    }

    private void ArmSave(TimeSpan delay)
    {
        try
        {
            _saveTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Encerrando.
        }
    }

    private void ScheduleReload(TimeSpan delay)
    {
        try
        {
            _reloadTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Encerrando.
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        // Ultima chance de ler um arquivo que estava bloqueado: se der, as mudancas
        // feitas nesse meio tempo sao reaplicadas por cima dele e entram na gravacao final.
        if (IsDiskUnreadable())
        {
            try
            {
                ReloadFromDisk();
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Falha ao tentar ler o arquivo de configurações ao encerrar");
            }
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
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

        try
        {
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _reloadTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }

        // Grava o que ficou pendente. O cadeado de gravacao faz esta chamada esperar uma
        // gravacao do temporizador que esteja no meio: encerrar o programa nao pode perder
        // o ajuste que o usuario acabou de fazer, nem deixar um arquivo pela metade.
        SaveCore();

        lock (_gate)
        {
            if (_savePending)
            {
                _log.Warning("Encerrando com configurações que não foram para o disco.");
            }
        }

        _saveTimer.Dispose();
        _reloadTimer.Dispose();
    }
}
