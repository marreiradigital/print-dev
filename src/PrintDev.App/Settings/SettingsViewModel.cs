using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Paths;
using PrintDev.Core.Runtime;
using PrintDev.Core.Storage;

namespace PrintDev.Settings;

/// <summary>Uma opção de lista suspensa, com o rótulo já em português.</summary>
/// <param name="Value">O valor que vai para as configurações.</param>
/// <param name="Label">O que o usuário lê.</param>
public sealed record Option<T>(T Value, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// A ponte entre o painel de configurações e o arquivo.
/// <para>
/// Cada propriedade lê direto de <see cref="ISettingsService.Current"/> e escreve por
/// <see cref="ISettingsService.Update"/>. Não existe cópia intermediária, e por isso não
/// existe o estado "o painel mostra uma coisa e o arquivo tem outra" — que é o defeito
/// clássico de painel de configurações com botão OK.
/// </para>
/// <para>
/// Também não há botão OK, de propósito: a mudança vale na hora, e a gravação em disco é
/// adiada meio segundo pelo próprio serviço. Arrastar um controle deslizante dispara
/// dezenas de mudanças por segundo e não pode virar dezenas de gravações.
/// </para>
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly AutoStartService _autoStart;

    public SettingsViewModel(ISettingsService settings, IAppPaths paths, AutoStartService autoStart)
    {
        _settings = settings;
        _paths = paths;
        _autoStart = autoStart;

        // Editar o settings.json na mao com o painel aberto tem que refletir na tela.
        _settings.Changed += (_, _) => RefreshAll();
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    // ==================== Geral ====================

    /// <summary>
    /// Le do SISTEMA, e nao das configuracoes: quem manda e o registro do Windows. Se o
    /// usuario desligar a entrada pelo Gerenciador de Tarefas, o painel tem que mostrar
    /// desligado - e nao o que o nosso arquivo achava que era verdade.
    /// </summary>
    public bool StartWithWindows
    {
        get => _autoStart.IsRunKeyEnabled || _autoStart.IsElevatedTaskInstalled;
        set
        {
            if (StartElevated)
            {
                _autoStart.RequestElevatedTask(value);
            }
            else
            {
                _autoStart.SetRunKey(value);
            }

            Write(s => s with { General = s.General with { StartWithWindows = value } });
            Notify(nameof(StartElevated));
        }
    }

    public bool StartElevated
    {
        get => _autoStart.IsElevatedTaskInstalled;
        set
        {
            // Alternar entre as duas formas: sai de uma e entra na outra. Manter as duas
            // faria o programa ser aberto duas vezes no logon.
            if (value)
            {
                _autoStart.RequestElevatedTask(install: true);
            }
            else
            {
                _autoStart.RequestElevatedTask(install: false);

                if (StartWithWindows)
                {
                    _autoStart.SetRunKey(true);
                }
            }

            Write(s => s with { General = s.General with { StartElevated = value } });
            Notify(nameof(StartWithWindows));
        }
    }

    public Option<ThemeMode> Theme
    {
        get => Find(Themes, Read(s => s.General.Theme));
        set => Write(s => s with { General = s.General with { Theme = value.Value } });
    }

    public static IReadOnlyList<Option<ThemeMode>> Themes { get; } =
    [
        new(ThemeMode.Sistema, "Acompanhar o Windows"),
        new(ThemeMode.Escuro, "Escuro"),
        new(ThemeMode.Claro, "Claro"),
    ];

    // ==================== Captura ====================

    public Option<CaptureMode> DefaultMode
    {
        get => Find(Modes, Read(s => s.Capture.DefaultMode));
        set => Write(s => s with { Capture = s.Capture with { DefaultMode = value.Value } });
    }

    public static IReadOnlyList<Option<CaptureMode>> Modes { get; } =
    [
        new(CaptureMode.Regiao, "Região"),
        new(CaptureMode.Janela, "Janela"),
        new(CaptureMode.Monitor, "Monitor"),
        new(CaptureMode.TelaInteira, "Tudo"),
    ];

    public double VeilOpacity
    {
        get => Math.Round(Read(s => s.Capture.VeilOpacity) * 100);
        set => Write(s => s with { Capture = s.Capture with { VeilOpacity = value / 100 } });
    }

    public bool ShowMagnifier
    {
        get => Read(s => s.Capture.ShowMagnifier);
        set => Write(s => s with { Capture = s.Capture with { ShowMagnifier = value } });
    }

    public double MagnifierZoom
    {
        get => Read(s => s.Capture.MagnifierZoom);
        set => Write(s => s with { Capture = s.Capture with { MagnifierZoom = (int)value } });
    }

    public bool ShowCrosshair
    {
        get => Read(s => s.Capture.ShowCrosshair);
        set => Write(s => s with { Capture = s.Capture with { ShowCrosshair = value } });
    }

    public bool IncludeCursor
    {
        get => Read(s => s.Capture.IncludeCursor);
        set => Write(s => s with { Capture = s.Capture with { IncludeCursor = value } });
    }

    public bool ShowToast
    {
        get => Read(s => s.Capture.ShowToast);
        set => Write(s => s with { Capture = s.Capture with { ShowToast = value } });
    }

    public double ToastSeconds
    {
        get => Read(s => s.Capture.ToastSeconds);
        set => Write(s => s with { Capture = s.Capture with { ToastSeconds = Math.Round(value) } });
    }

    // ==================== Salvamento ====================

    public string Folder
    {
        get => CaptureFolderResolver.ResolveRoot(_settings.Current, _paths);
        set => Write(s => s with { Save = s.Save with { Folder = value } });
    }

    public Option<DateSubfolder> DateSubfolder
    {
        get => Find(DateSubfolders, Read(s => s.Save.DateSubfolder));
        set => Write(s => s with { Save = s.Save with { DateSubfolder = value.Value } });
    }

    public static IReadOnlyList<Option<DateSubfolder>> DateSubfolders { get; } =
    [
        new(Core.Configuration.DateSubfolder.Nenhuma, "Tudo na mesma pasta"),
        new(Core.Configuration.DateSubfolder.Ano, "Uma pasta por ano"),
        new(Core.Configuration.DateSubfolder.AnoMes, "Ano e mês"),
        new(Core.Configuration.DateSubfolder.AnoMesDia, "Ano, mês e dia"),
    ];

    public Option<ImageFormat> Format
    {
        get => Find(Formats, Read(s => s.Save.Format));
        set => Write(s => s with { Save = s.Save with { Format = value.Value } });
    }

    public static IReadOnlyList<Option<ImageFormat>> Formats { get; } =
    [
        new(ImageFormat.Png, "PNG — sem perda"),
        new(ImageFormat.Jpeg, "JPEG — arquivo menor"),
    ];

    /// <summary>A qualidade só faz sentido em JPEG.</summary>
    public bool JpegQualityEnabled => Read(s => s.Save.Format) == ImageFormat.Jpeg;

    public double JpegQuality
    {
        get => Read(s => s.Save.JpegQuality);
        set => Write(s => s with { Save = s.Save with { JpegQuality = (int)value } });
    }

    public string NameTemplate
    {
        get => Read(s => s.Save.NameTemplate);
        set => Write(s => s with { Save = s.Save with { NameTemplate = value } });
    }

    /// <summary>
    /// Como o nome do próximo arquivo vai ficar. É a diferença entre o usuário entender
    /// os marcadores na hora e ter que capturar para descobrir.
    /// </summary>
    public string NamePreview
    {
        get
        {
            AppSettings current = _settings.Current;
            var context = new FileNameContext(
                When: DateTime.Now,
                AppName: "chrome",
                WindowTitle: "Exemplo",
                MonitorName: "DISPLAY1",
                Width: 1280,
                Height: 720,
                Mode: "regiao",
                Counter: 1);

            return FileNameTemplate.Render(current.Save.NameTemplate, context)
                   + ImageWriter.ExtensionOf(current.Save.Format);
        }
    }

    public Option<NameCollision> OnNameCollision
    {
        get => Find(Collisions, Read(s => s.Save.OnNameCollision));
        set => Write(s => s with { Save = s.Save with { OnNameCollision = value.Value } });
    }

    public static IReadOnlyList<Option<NameCollision>> Collisions { get; } =
    [
        new(NameCollision.SufixoNumerico, "Acrescentar _2, _3…"),
        new(NameCollision.Milissegundos, "Acrescentar os milissegundos"),
        new(NameCollision.Sobrescrever, "Sobrescrever"),
    ];

    public bool OpenFolderAfterSave
    {
        get => Read(s => s.Save.OpenFolderAfterSave);
        set => Write(s => s with { Save = s.Save with { OpenFolderAfterSave = value } });
    }

    // ==================== Área de transferência ====================

    public bool CopyAutomatically
    {
        get => Read(s => s.Clipboard.CopyAutomatically);
        set => Write(s => s with { Clipboard = s.Clipboard with { CopyAutomatically = value } });
    }

    public Option<ClipboardContent> ClipboardContent
    {
        get => Find(Contents, Read(s => s.Clipboard.Content));
        set => Write(s => s with { Clipboard = s.Clipboard with { Content = value.Value } });
    }

    public static IReadOnlyList<Option<ClipboardContent>> Contents { get; } =
    [
        new(Core.Configuration.ClipboardContent.ImagemECaminho, "Imagem e caminho (o destino escolhe)"),
        new(Core.Configuration.ClipboardContent.Imagem, "Só a imagem"),
        new(Core.Configuration.ClipboardContent.Caminho, "Só o caminho"),
    ];

    public Option<PathTextFormat> PathFormat
    {
        get => Find(PathFormats, Read(s => s.Clipboard.PathFormat));
        set => Write(s => s with { Clipboard = s.Clipboard with { PathFormat = value.Value } });
    }

    public static IReadOnlyList<Option<PathTextFormat>> PathFormats { get; } =
    [
        new(PathTextFormat.Windows, "Caminho do Windows"),
        new(PathTextFormat.BarraNormal, "Com barra normal"),
        new(PathTextFormat.Wsl, "Caminho do WSL"),
        new(PathTextFormat.UriFile, "Endereço file://"),
        new(PathTextFormat.Markdown, "Markdown"),
        new(PathTextFormat.Html, "HTML"),
        new(PathTextFormat.SomenteNome, "Só o nome do arquivo"),
    ];

    /// <summary>
    /// O texto EXATO que será colado. Sem isto, escolher entre sete formatos é adivinhar.
    /// </summary>
    public string PathPreview
    {
        get
        {
            AppSettings current = _settings.Current;
            string example = Path.Combine(
                CaptureFolderResolver.ResolveRoot(current, _paths),
                "PrintDev_2026-09-07_143210" + ImageWriter.ExtensionOf(current.Save.Format));

            return PathTextFormatter.Format(example, current.Clipboard);
        }
    }

    public Option<QuoteMode> Quotes
    {
        get => Find(QuoteModes, Read(s => s.Clipboard.Quotes));
        set => Write(s => s with { Clipboard = s.Clipboard with { Quotes = value.Value } });
    }

    public static IReadOnlyList<Option<QuoteMode>> QuoteModes { get; } =
    [
        new(QuoteMode.QuandoTiverEspaco, "Só quando houver espaço"),
        new(QuoteMode.Nunca, "Nunca"),
        new(QuoteMode.Sempre, "Sempre"),
    ];

    public string WslRoot
    {
        get => Read(s => s.Clipboard.WslRoot);
        set => Write(s => s with { Clipboard = s.Clipboard with { WslRoot = value } });
    }

    public bool IncludeFileDrop
    {
        get => Read(s => s.Clipboard.IncludeFileDrop);
        set => Write(s => s with { Clipboard = s.Clipboard with { IncludeFileDrop = value } });
    }

    public bool IncludeHtml
    {
        get => Read(s => s.Clipboard.IncludeHtml);
        set => Write(s => s with { Clipboard = s.Clipboard with { IncludeHtml = value } });
    }

    // ==================== Atalhos ====================

    public string HotkeyCapture
    {
        get => Read(s => s.Hotkeys.Capture);
        set => Write(s => s with { Hotkeys = s.Hotkeys with { Capture = value } });
    }

    public string HotkeyFullScreen
    {
        get => Read(s => s.Hotkeys.FullScreen);
        set => Write(s => s with { Hotkeys = s.Hotkeys with { FullScreen = value } });
    }

    public string HotkeyActiveWindow
    {
        get => Read(s => s.Hotkeys.ActiveWindow);
        set => Write(s => s with { Hotkeys = s.Hotkeys with { ActiveWindow = value } });
    }

    public string HotkeyRepeatLastRegion
    {
        get => Read(s => s.Hotkeys.RepeatLastRegion);
        set => Write(s => s with { Hotkeys = s.Hotkeys with { RepeatLastRegion = value } });
    }

    public string HotkeyPasteAsPath
    {
        get => Read(s => s.Hotkeys.PasteAsPath);
        set => Write(s => s with { Hotkeys = s.Hotkeys with { PasteAsPath = value } });
    }

    public string HotkeyPasteAsImage
    {
        get => Read(s => s.Hotkeys.PasteAsImage);
        set => Write(s => s with { Hotkeys = s.Hotkeys with { PasteAsImage = value } });
    }

    // ==================== Histórico e limpeza ====================

    public double HistoryCount
    {
        get => Read(s => s.History.Count);
        set => Write(s => s with { History = s.History with { Count = (int)value } });
    }

    public bool HistoryInTray
    {
        get => Read(s => s.History.ShowInTray);
        set => Write(s => s with { History = s.History with { ShowInTray = value } });
    }

    public bool CleanupEnabled
    {
        get => Read(s => s.Cleanup.Enabled);
        set => Write(s => s with { Cleanup = s.Cleanup with { Enabled = value } });
    }

    public double CleanupKeepDays
    {
        get => Read(s => s.Cleanup.KeepDays);
        set => Write(s => s with { Cleanup = s.Cleanup with { KeepDays = (int)value } });
    }

    public bool CleanupToRecycleBin
    {
        get => Read(s => s.Cleanup.MoveToRecycleBin);
        set => Write(s => s with { Cleanup = s.Cleanup with { MoveToRecycleBin = value } });
    }

    public bool CleanupOnlyOurFiles
    {
        get => Read(s => s.Cleanup.OnlyPrintDevFiles);
        set => Write(s => s with { Cleanup = s.Cleanup with { OnlyPrintDevFiles = value } });
    }

    // ==================== Avançado ====================

    public Option<string> LogLevel
    {
        get => Find(LogLevels, Read(s => s.Advanced.LogLevel));
        set => Write(s => s with { Advanced = s.Advanced with { LogLevel = value.Value } });
    }

    public static IReadOnlyList<Option<string>> LogLevels { get; } =
    [
        new("Information", "Normal"),
        new("Debug", "Detalhado"),
        new("Verbose", "Tudo"),
        new("Warning", "Só avisos"),
        new("Error", "Só erros"),
    ];

    public Option<CompetitorPolicy> CompetitorPolicy
    {
        get => Find(CompetitorPolicies, Read(s => s.Advanced.OnCompetitorDetected));
        set => Write(s => s with { Advanced = s.Advanced with { OnCompetitorDetected = value.Value } });
    }

    public static IReadOnlyList<Option<CompetitorPolicy>> CompetitorPolicies { get; } =
    [
        new(Core.Configuration.CompetitorPolicy.Perguntar, "Avisar quem está com a tecla"),
        new(Core.Configuration.CompetitorPolicy.AssumirAutomaticamente, "Encerrar o outro e assumir"),
        new(Core.Configuration.CompetitorPolicy.SomenteAvisar, "Só registrar no log"),
    ];

    public bool GuardHotkey
    {
        get => Read(s => s.Advanced.GuardHotkey);
        set => Write(s => s with { Advanced = s.Advanced with { GuardHotkey = value } });
    }

    // ==================== Sobre ====================

    /// <summary>Pasta onde o arquivo de configurações vive.</summary>
    public string SettingsFolder => _paths.SettingsDirectory;

    /// <summary>Pasta dos logs.</summary>
    public string LogsFolder => _paths.LogsDirectory;

    /// <summary>Versão do programa.</summary>
    public string Version =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Volta tudo ao padrão de fábrica.</summary>
    public void RestoreDefaults()
    {
        _settings.Update(_ => new AppSettings());
        RefreshAll();
    }

    private T Read<T>(Func<AppSettings, T> read) => read(_settings.Current);

    private void Write(Func<AppSettings, AppSettings> change, [CallerMemberName] string? property = null)
    {
        _settings.Update(change);
        Notify(property);

        // As previas e as dependencias entre campos (a qualidade do JPEG so vale em
        // JPEG) sao recalculadas a cada mudanca. E barato e evita a classe de defeito em
        // que a previa mostra o formato anterior.
        Notify(nameof(NamePreview));
        Notify(nameof(PathPreview));
        Notify(nameof(JpegQualityEnabled));
    }

    private static Option<T> Find<T>(IReadOnlyList<Option<T>> options, T value)
        => options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ?? options[0];

    private void Notify([CallerMemberName] string? property = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    /// <summary>Reavisa tudo. Usado quando o arquivo muda por fora.</summary>
    private void RefreshAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
