using System.Windows;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Infrastructure;
using Serilog;

namespace PrintDev.Settings;

/// <summary>
/// Abre o painel de configurações, reaproveitando a janela quando ela já está aberta.
/// <para>
/// Uma janela por vez: abrir uma segunda deixaria duas telas editando o mesmo arquivo,
/// e o usuário veria uma delas desatualizada.
/// </para>
/// </summary>
public sealed class SettingsWindowHost
{
    private readonly SettingsViewModel _model;
    private readonly IAppPaths _paths;
    private readonly HotkeyManager _hotkeys;
    private readonly ILogger _log;

    private SettingsWindow? _window;

    public SettingsWindowHost(SettingsViewModel model, IAppPaths paths, HotkeyManager hotkeys, ILogger log)
    {
        _model = model;
        _paths = paths;
        _hotkeys = hotkeys;
        _log = log.ForContext<SettingsWindowHost>();
    }

    /// <summary>Abre o painel, ou traz para a frente o que já estava aberto.</summary>
    public void Show()
    {
        if (_window is not null)
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }

            _window.Activate();
            return;
        }

        _window = new SettingsWindow(_model, _paths, _hotkeys);
        _window.Closed += (_, _) => _window = null;
        _window.Show();

        _log.Debug("Painel de configurações aberto");
    }
}
