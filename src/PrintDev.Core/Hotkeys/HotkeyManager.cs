using System.ComponentModel;
using System.Runtime.InteropServices;
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
public sealed record HotkeyRegistration(
    HotkeyAction Action,
    string Text,
    HotkeyDefinition Hotkey,
    bool Registered,
    string? Error);

/// <summary>
/// Registra os atalhos globais e avisa quando algum é acionado.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HotkeyMessageWindow _window;
    private readonly ILogger _log;
    private readonly Dictionary<int, HotkeyAction> _actionsById = [];
    private readonly Dictionary<HotkeyAction, HotkeyRegistration> _registrations = [];
    private bool _disposed;

    public HotkeyManager(HotkeyMessageWindow window, ILogger log)
    {
        _window = window;
        _log = log.ForContext<HotkeyManager>();
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>Disparado quando o usuário aciona um atalho registrado.</summary>
    public event EventHandler<HotkeyAction>? Triggered;

    /// <summary>Situação atual de cada atalho, para a interface mostrar conflitos.</summary>
    public IReadOnlyDictionary<HotkeyAction, HotkeyRegistration> Registrations => _registrations;

    /// <summary>
    /// Atalhos que a configuração pede mas o Windows recusou. É o que o vigia observa
    /// para tentar de novo depois.
    /// </summary>
    public IReadOnlyList<HotkeyRegistration> Failures =>
        _registrations.Values.Where(r => r.Hotkey.IsSet && !r.Registered).ToList();

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

        UnregisterAll();

        foreach ((HotkeyAction action, string text) in Enumerate(settings))
        {
            _registrations[action] = Register(action, text);
        }

        int ok = _registrations.Values.Count(r => r.Registered);
        _log.Information("Atalhos registrados: {Registrados} de {Total}", ok, _registrations.Count);
    }

    /// <summary>Tenta registrar de novo apenas o que falhou antes.</summary>
    /// <returns>Quantos passaram a funcionar agora.</returns>
    public int RetryFailures()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int recovered = 0;

        foreach (HotkeyRegistration failure in Failures)
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
        return new HotkeyRegistration(action, text, hotkey, false, message);
    }

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
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        if (_actionsById.TryGetValue(id, out HotkeyAction action))
        {
            Triggered?.Invoke(this, action);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.HotkeyPressed -= OnHotkeyPressed;
        UnregisterAll();
    }
}
