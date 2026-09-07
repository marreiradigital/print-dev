using System.Windows;
using System.Windows.Interop;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Maintenance;
using PrintDev.Core.Screens;
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
    private readonly CleanupService _cleanup;
    private readonly ILogger _log;

    private SettingsWindow? _window;

    public SettingsWindowHost(
        SettingsViewModel model,
        IAppPaths paths,
        HotkeyManager hotkeys,
        CleanupService cleanup,
        ILogger log)
    {
        _model = model;
        _paths = paths;
        _hotkeys = hotkeys;
        _cleanup = cleanup;
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
            BringToFront(_window);
            return;
        }

        _window = new SettingsWindow(_model, _paths, _hotkeys, _cleanup);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        BringToFront(_window);

        _log.Debug("Painel de configurações aberto");
    }

    /// <summary>
    /// Insiste no primeiro plano depois de mostrar a janela.
    /// <para>
    /// O <c>Activate</c> do WPF costuma bastar, mas quando a chamada vem de um clique no
    /// ícone da bandeja quem está em primeiro plano é a barra de tarefas, e a janela pode
    /// nascer atrás dela piscando na barra. O Windows aceita a promoção porque o processo
    /// acabou de receber o clique.
    /// </para>
    /// </summary>
    private static void BringToFront(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;

        if (handle != IntPtr.Zero)
        {
            WindowPlacement.BringToFront(handle);
        }
    }
}
