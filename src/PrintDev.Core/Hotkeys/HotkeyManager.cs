using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using PrintDev.Core.Configuration;
using PrintDev.Core.Interop;
using Serilog;

namespace PrintDev.Core.Hotkeys;

/// <summary>Situação de um atalho depois da tentativa de registro.</summary>
/// <param name="Action">A ação que ele dispara.</param>
/// <param name="Text">Como está escrito nas configurações.</param>
/// <param name="Hotkey">Atalho interpretado.</param>
/// <param name="Registered">Se o Windows aceitou.</param>
/// <param name="Error">Motivo da recusa, em português, pronto para a interface.</param>
/// <param name="Retryable">
/// Se vale tentar de novo mais tarde: só a recusa do próprio Windows (outro programa pode
/// soltar a tecla). Texto que não se lê e combinação repetida dentro do Print Dev só
/// mudam quando a configuração muda.
/// </param>
public sealed record HotkeyRegistration(
    HotkeyAction Action,
    string Text,
    HotkeyDefinition Hotkey,
    bool Registered,
    string? Error,
    bool Retryable = false);

/// <summary>
/// Registra os atalhos globais e avisa quando algum é acionado.
/// <para>
/// <c>RegisterHotKey</c> só funciona na thread dona da janela: de outra thread falha com
/// <c>ERROR_WINDOW_OF_OTHER_THREAD</c>, e a falha parece "tecla já usada por outro
/// programa". Por isso todo trabalho que toca o registro é devolvido, aqui dentro, para o
/// <see cref="Dispatcher"/> dessa thread — quem chama não precisa saber de que thread vem.
/// As leituras (<see cref="Failures"/>, <see cref="Registrations"/>) são seguras de
/// qualquer thread e devolvem uma cópia.
/// </para>
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HotkeyMessageWindow _window;
    private readonly ILogger _log;
    private readonly Dispatcher _dispatcher;

    // Protege tudo abaixo. O registro em si so acontece na thread da janela, mas a
    // interface e o vigia leem o estado de la tambem.
    private readonly object _gate = new();
    private readonly Dictionary<int, HotkeyAction> _actionsById = [];
    private readonly Dictionary<HotkeyAction, HotkeyRegistration> _registrations = [];
    private HotkeySettings? _settings;
    private int _suspensions;
    private bool _disposed;

    public HotkeyManager(HotkeyMessageWindow window, ILogger log)
    {
        _window = window;
        _log = log.ForContext<HotkeyManager>();

        // O dono da janela e quem manda: e a thread dele que pode registrar atalho.
        _dispatcher = HwndSource.FromHwnd(window.Handle)?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>Disparado quando o usuário aciona um atalho registrado.</summary>
    public event EventHandler<HotkeyAction>? Triggered;

    /// <summary>
    /// O despachante da thread dona da janela dos atalhos: é nela que o registro
    /// precisa acontecer, e onde o vigia precisa rodar.
    /// </summary>
    internal Dispatcher Dispatcher => _dispatcher;

    /// <summary>Situação atual de cada atalho, para a interface mostrar conflitos.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> Registrations
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<HotkeyAction, HotkeyRegistration>(_registrations);
            }
        }
    }

    /// <summary>
    /// Tudo que a configuração pede e não está valendo, com o motivo: o Windows recusou,
    /// o texto não se lê (atalho escrito errado à mão no arquivo) ou a combinação já é de
    /// outra ação do Print Dev. É o que a tela de atalhos mostra.
    /// <para>
    /// Atalho em branco não entra: é "sem atalho", escolha válida. E o filtro NÃO pode ser
    /// "tem tecla": um texto que não se lê vira <see cref="HotkeyDefinition.None"/>, e por
    /// esse filtro o erro dele nunca aparecia em lugar nenhum.
    /// </para>
    /// </summary>
    public IReadOnlyList<HotkeyRegistration> Failures
    {
        get
        {
            lock (_gate)
            {
                return _registrations.Values.Where(r => !r.Registered && r.Error is not null).ToList();
            }
        }
    }

    /// <summary>
    /// A parte de <see cref="Failures"/> em que vale insistir: a recusa do próprio Windows.
    /// É o que o vigia observa — um texto ilegível ou uma combinação repetida não melhoram
    /// sozinhos, e tentar de novo a cada trinta segundos só mentiria no log que "outro
    /// programa" está com a tecla.
    /// </summary>
    public IReadOnlyList<HotkeyRegistration> RetryableFailures
    {
        get
        {
            lock (_gate)
            {
                return _registrations.Values.Where(r => !r.Registered && r.Retryable).ToList();
            }
        }
    }

    /// <summary>
    /// Solta tudo que estava registrado e registra de novo a partir das configurações.
    /// <para>
    /// Registrar tudo de novo, em vez de calcular a diferença, é de propósito: o
    /// caminho é barato e a alternativa carrega o risco de deixar um atalho órfão
    /// registrado com uma tecla que já não está mais nas configurações.
    /// </para>
    /// </summary>
    public void Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ObjectDisposedException.ThrowIf(_disposed, this);

        OnOwnerThread(() =>
        {
            ApplyCore(settings);
            return 0;
        });
    }

    private void ApplyCore(HotkeySettings settings)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _settings = settings;

            if (_suspensions > 0)
            {
                // Suspenso: guarda o que pediram e registra quando voltar.
                return;
            }

            UnregisterAll();

            foreach ((HotkeyAction action, string text) in Enumerate(settings))
            {
                _registrations[action] = Register(action, text);
            }

            int ok = _registrations.Values.Count(r => r.Registered);
            _log.Information("Atalhos registrados: {Registrados} de {Total}", ok, _registrations.Count);
        }
    }

    /// <summary>
    /// Solta todos os atalhos até <see cref="Resume"/>.
    /// <para>
    /// É o que deixa gravar um atalho apertando as teclas no painel. Um atalho registrado
    /// é engolido pelo Windows antes de chegar a qualquer janela: sem soltar, apertar
    /// <c>PrtSc</c> no campo abriria o seletor em vez de ser gravado.
    /// </para>
    /// </summary>
    public void Suspend()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        OnOwnerThread(() =>
        {
            lock (_gate)
            {
                if (!_disposed && _suspensions++ == 0)
                {
                    UnregisterAll();
                    _log.Debug("Atalhos suspensos");
                }
            }

            return 0;
        });
    }

    /// <summary>Registra de novo o que estava valendo antes de <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        if (_disposed)
        {
            return;
        }

        OnOwnerThread(() =>
        {
            HotkeySettings? toApply = null;

            lock (_gate)
            {
                if (_disposed || _suspensions == 0)
                {
                    return 0;
                }

                if (--_suspensions == 0)
                {
                    toApply = _settings;
                }
            }

            if (toApply is not null)
            {
                ApplyCore(toApply);
            }

            return 0;
        });
    }

    /// <summary>Tenta registrar de novo apenas o que falhou antes.</summary>
    /// <returns>Quantos passaram a funcionar agora.</returns>
    public int RetryFailures()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return OnOwnerThread(RetryFailuresCore);
    }

    private int RetryFailuresCore()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            int recovered = 0;

            foreach (HotkeyRegistration failure in RetryableFailures)
            {
                HotkeyRegistration retried = Register(failure.Action, failure.Text);
                _registrations[failure.Action] = retried;

                if (retried.Registered)
                {
                    recovered++;
                    _log.Information("Atalho {Acao} ({Atalho}) foi assumido", failure.Action, failure.Text);
                }
            }

            return recovered;
        }
    }

    /// <summary>
    /// Executa na thread dona da janela, esperando o resultado. Já estando nela, é uma
    /// chamada direta.
    /// </summary>
    private T OnOwnerThread<T>(Func<T> work)
    {
        if (_dispatcher.CheckAccess())
        {
            return work();
        }

        // Dispatcher que ja encerrou nao executa mais nada: o programa esta saindo.
        return _dispatcher.HasShutdownFinished ? default! : _dispatcher.Invoke(work);
    }

    // Chamar so com _gate seguro, na thread da janela.
    private HotkeyRegistration Register(HotkeyAction action, string text)
    {
        if (!HotkeyParser.TryParse(text, out HotkeyDefinition hotkey, out string? parseError))
        {
            _log.Warning("Atalho inválido para {Acao}: {Erro}", action, parseError);
            return new HotkeyRegistration(action, text, HotkeyDefinition.None, false, parseError);
        }

        if (!hotkey.IsSet)
        {
            // Atalho em branco e escolha valida: a acao existe, so nao tem tecla.
            return new HotkeyRegistration(action, text, hotkey, false, null);
        }

        // Duas acoes do proprio Print Dev na mesma combinacao: o Windows recusa a segunda
        // com o mesmo erro de "ja registrado", e a mensagem dizia "usado por outro
        // programa" -- o usuario ia procurar o culpado fora, quando e so trocar uma delas.
        foreach (HotkeyRegistration other in _registrations.Values)
        {
            if (other.Registered && other.Action != action && other.Hotkey == hotkey)
            {
                string duplicate = $"O atalho {text} já está atribuído a \"{LabelOf(other.Action)}\". Cada ação precisa de uma combinação própria.";

                _log.Warning("Atalho repetido: {Acao} e {Outra} usam {Atalho}", action, other.Action, text);
                return new HotkeyRegistration(action, text, hotkey, false, duplicate);
            }
        }

        int id = (int)action;

        if (NativeMethods.RegisterHotKey(_window.Handle, id, hotkey.ModifiersForRegistration, hotkey.VirtualKey))
        {
            _actionsById[id] = action;
            return new HotkeyRegistration(action, text, hotkey, true, null);
        }

        int lastError = Marshal.GetLastWin32Error();
        string message = lastError == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
            ? $"O atalho {text} já está sendo usado por outro programa."
            : $"O Windows recusou o atalho {text}: {new Win32Exception(lastError).Message}";

        _log.Warning("Falha ao registrar {Acao} ({Atalho}): {Erro}", action, text, message);
        return new HotkeyRegistration(action, text, hotkey, false, message, Retryable: true);
    }

    /// <summary>O nome da ação como o painel de atalhos a mostra.</summary>
    private static string LabelOf(HotkeyAction action) => action switch
    {
        HotkeyAction.Capture => "Abrir o seletor",
        HotkeyAction.FullScreen => "Capturar o monitor atual",
        HotkeyAction.ActiveWindow => "Capturar a janela ativa",
        HotkeyAction.RepeatLastRegion => "Repetir a última região",
        HotkeyAction.PasteAsPath => "Colar como caminho",
        HotkeyAction.PasteAsImage => "Colar como imagem",
        HotkeyAction.ColorPicker => "Conta-gotas de cor",
        HotkeyAction.Ocr => "Reconhecer texto na tela",
        HotkeyAction.UndoLastCapture => "Desfazer a última captura",
        HotkeyAction.SendToCloud => "Enviar a última captura para a nuvem",
        _ => action.ToString(),
    };

    // Chamar so com _gate seguro, na thread da janela.
    private void UnregisterAll()
    {
        foreach (int id in _actionsById.Keys)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
        }

        _actionsById.Clear();
        _registrations.Clear();
    }

    private static IEnumerable<(HotkeyAction Action, string Text)> Enumerate(HotkeySettings settings)
    {
        yield return (HotkeyAction.Capture, settings.Capture);
        yield return (HotkeyAction.FullScreen, settings.FullScreen);
        yield return (HotkeyAction.ActiveWindow, settings.ActiveWindow);
        yield return (HotkeyAction.RepeatLastRegion, settings.RepeatLastRegion);
        yield return (HotkeyAction.PasteAsPath, settings.PasteAsPath);
        yield return (HotkeyAction.PasteAsImage, settings.PasteAsImage);
        yield return (HotkeyAction.ColorPicker, settings.ColorPicker);
        yield return (HotkeyAction.Ocr, settings.Ocr);
        yield return (HotkeyAction.UndoLastCapture, settings.UndoLastCapture);
        yield return (HotkeyAction.SendToCloud, settings.SendToCloud);
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        HotkeyAction action;

        lock (_gate)
        {
            if (!_actionsById.TryGetValue(id, out action))
            {
                return;
            }
        }

        Triggered?.Invoke(this, action);
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

            _disposed = true;
        }

        _window.HotkeyPressed -= OnHotkeyPressed;

        lock (_gate)
        {
            UnregisterAll();
        }
    }
}
