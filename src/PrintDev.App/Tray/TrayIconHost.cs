using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using PrintDev.Core.Capture;
using PrintDev.Core.Clipboard;
using PrintDev.Core.Configuration;
using PrintDev.Core.History;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Paths;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Runtime;
using PrintDev.Core.Updates;
using PrintDev.Settings;
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
    private readonly CaptureHistory _history;
    private readonly ClipboardWriter _clipboard;
    private readonly HotkeyMessageWindow _messageWindow;
    private readonly SettingsWindowHost _settingsWindow;
    private readonly UpdateService _updates;
    private readonly ILogger _log;
    private TaskbarIcon? _icon;
    private MenuItem? _updateItem;

    /// <summary>Marca os itens do menu que sao recriados a cada abertura.</summary>
    private const string HistoryTag = "historico";

    public TrayIconHost(
        IAppPaths paths,
        ISettingsService settings,
        CaptureHistory history,
        ClipboardWriter clipboard,
        HotkeyMessageWindow messageWindow,
        SettingsWindowHost settingsWindow,
        UpdateService updates,
        ILogger log)
    {
        _paths = paths;
        _settings = settings;
        _history = history;
        _clipboard = clipboard;
        _messageWindow = messageWindow;
        _settingsWindow = settingsWindow;
        _updates = updates;
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
            ToolTipText = "Print Dev — clique para abrir",
            ContextMenu = BuildMenu(),
            MenuActivation = PopupActivationMode.RightClick,
        };

        // Clique com o botao esquerdo abre o painel. Sem isto o icone so responde ao
        // botao direito, e um clique comum nao faz nada - o que passa impressao de
        // programa quebrado, porque icone de bandeja abre no clique em todo lugar.
        //
        // O evento e o de SOLTAR o botao, e nao o comando de clique da biblioteca: o
        // comando so dispara depois de esgotada a janela de espera do duplo clique, e
        // meio segundo entre o clique e a janela aparecer parece programa travado.
        _icon.TrayLeftMouseUp += (_, _) => _settingsWindow.Show();

        _log.Information("Ícone da bandeja criado");
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();

        // O menu e remontado a cada abertura: o historico muda o tempo todo, e uma lista
        // congelada na inicializacao mostraria capturas que ja nao existem.
        menu.Opened += (_, _) => Rebuild(menu);

        menu.Items.Add(MenuItemFor("Configurações", _settingsWindow.Show));
        menu.Items.Add(new Separator());

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

        _updateItem = new MenuItem();
        _updateItem.Click += (_, _) => OnUpdateClicked();
        RefreshUpdateItem();
        menu.Items.Add(_updateItem);

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItemFor("Sair", () =>
        {
            _log.Information("Encerrando pelo menu da bandeja");
            Application.Current.Shutdown(0);
        }));

        return menu;
    }

    /// <summary>
    /// Remonta o menu, com o submenu de capturas recentes atualizado.
    /// </summary>
    private void Rebuild(ContextMenu menu)
    {
        RefreshUpdateItem();

        // Tira o submenu anterior, se houver, e o recria com o estado de agora.
        foreach (object item in menu.Items.OfType<object>().Where(i => i is MenuItem { Tag: HistoryTag }).ToList())
        {
            menu.Items.Remove(item);
        }

        if (!_settings.Current.History.ShowInTray)
        {
            return;
        }

        IReadOnlyList<CaptureHistoryItem> items = _history.Items;
        var submenu = new MenuItem { Header = "Capturas recentes", Tag = HistoryTag };

        if (items.Count == 0)
        {
            submenu.Items.Add(new MenuItem { Header = "Nada capturado ainda", IsEnabled = false });
        }
        else
        {
            foreach (CaptureHistoryItem item in items)
            {
                submenu.Items.Add(HistoryEntry(item));
            }

            submenu.Items.Add(new Separator());
            submenu.Items.Add(MenuItemFor("Limpar a lista", _history.Clear));
        }

        menu.Items.Insert(0, submenu);
        menu.Items.Insert(1, new Separator { Tag = HistoryTag });
    }

    private MenuItem HistoryEntry(CaptureHistoryItem item)
    {
        var entry = new MenuItem
        {
            Header = $"{item.DisplayName}   {item.Width}×{item.Height}",
            Icon = new Image { Source = item.Thumbnail, Width = 32, Height = 22, Stretch = Stretch.UniformToFill },
            IsEnabled = item.StillExists || item.Path is null,
        };

        entry.Click += (_, _) => Recopy(item);
        return entry;
    }

    /// <summary>
    /// Copia de novo uma captura do histórico.
    /// <para>
    /// É o caso mais comum de uso do histórico: a captura foi copiada, colada no lugar
    /// errado, e a área de transferência já foi sobrescrita por outra coisa.
    /// </para>
    /// </summary>
    private void Recopy(CaptureHistoryItem item)
    {
        ClipboardSettings clipboard = _settings.Current.Clipboard;
        string? text = item.Path is null ? null : PathTextFormatter.Format(item.Path, clipboard);

        // A imagem vem do ARQUIVO, e nao do historico: o historico guarda so miniaturas,
        // porque vinte capturas de tela cheia em memoria passariam de cem megabytes.
        // Recopiar tem que devolver a imagem inteira, senao o recurso nao serve para o
        // que existe - colar de novo o que ja foi capturado.
        CapturedImage? image = item.Path is null ? null : CapturedImage.FromFile(item.Path);

        if (image is null && text is null)
        {
            _log.Warning("Não há mais nada para recopiar de {Arquivo}", item.DisplayName);
            return;
        }

        _clipboard.Write(
            _messageWindow.Handle,
            new ClipboardPayload(
                Image: image,
                Text: text,
                FilePath: item.Path,
                IncludeFileDrop: clipboard.IncludeFileDrop && item.Path is not null));

        _log.Information("Recopiado do histórico: {Arquivo}", item.DisplayName);
    }

    /// <summary>
    /// Reflete o estado da atualização no ícone.
    /// <para>
    /// Só a dica do ícone e o menu — nunca um balão, nunca uma janela. Avisar de
    /// versão nova não justifica roubar o foco de quem está digitando.
    /// </para>
    /// </summary>
    public void ShowUpdateStatus(UpdateCheckResult resultado)
    {
        ArgumentNullException.ThrowIfNull(resultado);

        RefreshUpdateItem();

        if (_icon is null)
        {
            return;
        }

        _icon.ToolTipText = resultado.Status switch
        {
            UpdateStatus.Baixado => $"Print Dev — atualização pronta ({resultado.Update?.Version.ToString(3)})",
            UpdateStatus.Disponivel => $"Print Dev — versão {resultado.Update?.Version.ToString(3)} disponível",
            _ => "Print Dev — clique para abrir",
        };
    }

    /// <summary>Põe no item do menu o rótulo que corresponde ao estado de agora.</summary>
    private void RefreshUpdateItem()
    {
        if (_updateItem is null)
        {
            return;
        }

        UpdateCheckResult resultado = _updates.Last;

        _updateItem.Header = resultado.Status switch
        {
            UpdateStatus.Baixado => $"Reiniciar e atualizar para a {resultado.Update?.Version.ToString(3)}",
            UpdateStatus.Disponivel => $"Baixar a versão {resultado.Update?.Version.ToString(3)}",
            _ => "Procurar atualizações",
        };
    }

    /// <summary>
    /// O clique faz a coisa mais adiantada possível: instala o que já está pronto,
    /// senão procura. Um item de menu que só reabre outra tela não ajuda ninguém.
    /// </summary>
    private void OnUpdateClicked()
    {
        if (_updates.Last.Status == UpdateStatus.Baixado)
        {
            if (_updates.InstalarAgora())
            {
                Application.Current.Shutdown(0);
            }

            return;
        }

        _ = Task.Run(async () =>
        {
            UpdateCheckResult resultado = await _updates.CheckAsync(forcado: true).ConfigureAwait(false);
            _log.Information("Procura manual de atualização: {Mensagem}", resultado.Message);
        });
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
