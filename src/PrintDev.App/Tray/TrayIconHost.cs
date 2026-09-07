using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Runtime;
using Serilog;

namespace PrintDev.Tray;

/// <summary>
/// Dono do ícone da bandeja e do menu que sai dele.
/// <para>
/// O menu é um <see cref="ContextMenu"/> do WPF, e não o menu nativo do shell,
/// justamente para poder ser estilizado junto com o resto do produto.
/// </para>
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly IAppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly ILogger _log;
    private TaskbarIcon? _icon;

    public TrayIconHost(IAppPaths paths, ISettingsService settings, ILogger log)
    {
        _paths = paths;
        _settings = settings;
        _log = log.ForContext<TrayIconHost>();
    }

    /// <summary>Cria o ícone e o coloca na bandeja.</summary>
    public void Show()
    {
        if (_icon is not null)
        {
            return;
        }

        _icon = new TaskbarIcon
        {
            // Renderiza o desenho vetorial no tamanho que o Windows pedir, por DPI,
            // em vez de depender de um .ico com resolucoes fixas.
            IconFrameworkElementSource = new TrayGlyph(),
            ToolTipText = "Print Dev",
            ContextMenu = BuildMenu(),
            MenuActivation = PopupActivationMode.RightClick,
        };

        _log.Information("Ícone da bandeja criado");
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItemFor(
            "Abrir pasta de capturas",
            () => OpenFolder(CaptureFolderResolver.ResolveRoot(_settings.Current, _paths))));

        menu.Items.Add(MenuItemFor(
            "Abrir pasta de logs",
            () => OpenFolder(_paths.LogsDirectory)));

        // Enquanto o painel de configuracoes nao existe, editar o JSON e o caminho
        // oficial - e ele continua valendo depois, porque o programa relê o arquivo
        // sozinho quando ele muda.
        menu.Items.Add(MenuItemFor(
            "Editar configurações (settings.json)",
            () => OpenFile(_paths.SettingsFile)));

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItemFor("Sair", () =>
        {
            _log.Information("Encerrando pelo menu da bandeja");
            Application.Current.Shutdown(0);
        }));

        return menu;
    }

    private static MenuItem MenuItemFor(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OpenFile(string path)
    {
        if (!ShellOpen.File(path))
        {
            _log.Warning("Não consegui abrir o arquivo {Arquivo}", path);
        }
    }

    private void OpenFolder(string path)
    {
        if (!ShellOpen.Folder(path))
        {
            _log.Warning("Não consegui abrir a pasta {Pasta}", path);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Sem o Dispose explicito o icone fica "fantasma" na bandeja ate o usuario
        // passar o mouse por cima - o shell so remove quando percebe que o processo
        // dono morreu.
        _icon?.Dispose();
        _icon = null;
    }
}
