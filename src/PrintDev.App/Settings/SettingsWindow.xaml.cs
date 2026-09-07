using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Maintenance;
using PrintDev.Core.Runtime;
using PrintDev.Core.Screens;

namespace PrintDev.Settings;

/// <summary>
/// O painel de configurações.
/// </summary>
public partial class SettingsWindow : Window
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x0002;

    private readonly SettingsViewModel _model;
    private readonly IAppPaths _paths;
    private readonly HotkeyManager _hotkeys;
    private readonly CleanupService _cleanup;
    private UIElement[] _pages = [];

    internal SettingsWindow(
        SettingsViewModel model, IAppPaths paths, HotkeyManager hotkeys, CleanupService cleanup)
    {
        _model = model;
        _paths = paths;
        _hotkeys = hotkeys;
        _cleanup = cleanup;

        InitializeComponent();
        DataContext = model;

        _pages =
        [
            PageGeneral, PageCapture, PageSave, PageClipboard,
            PageHotkeys, PageHistory, PageAdvanced, PageAbout,
        ];

        Rail.SelectionChanged += (_, _) => ShowPage(Rail.SelectedIndex);

        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximizeButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Close();

        ChooseFolderButton.Click += (_, _) => ChooseFolder();
        OpenLogsButton.Click += (_, _) => ShellOpen.Folder(_paths.LogsDirectory);
        OpenSettingsFileButton.Click += (_, _) => ShellOpen.File(_paths.SettingsFile);
        RestoreDefaultsButton.Click += (_, _) => _model.RestoreDefaults();
        SimulateCleanupButton.Click += (_, _) => SimulateCleanup();

        Loaded += (_, _) => RefreshHotkeyWarning();
    }

    /// <inheritdoc/>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        WindowPlacement.RoundCorners(handle);
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    /// <summary>
    /// Trata <c>WM_GETMINMAXINFO</c>.
    /// <para>
    /// Resolve DOIS defeitos com uma solução só. Sem isto, uma janela sem moldura
    /// nativa, ao maximizar, cobre a barra de tarefas <b>e</b> sangra oito pixels para
    /// fora da tela em cada lado — porque o Windows a posiciona em (-8, -8) contando com
    /// a moldura que aqui não existe. Travar o tamanho e a posição na área de trabalho do
    /// monitor corrige os dois.
    /// </para>
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO)
        {
            return IntPtr.Zero;
        }

        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);

        var monitorInfo = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(monitor, ref monitorInfo))
        {
            info.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.Monitor.Left;
            info.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.Monitor.Top;
            info.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
            info.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;

            // O tamanho minimo vem em PIXELS FISICOS. Sem multiplicar pela escala, a
            // janela ficaria com o minimo errado num monitor com escala diferente de 100%.
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            info.MinTrackSize.X = (int)(MinWidth * dpi.DpiScaleX);
            info.MinTrackSize.Y = (int)(MinHeight * dpi.DpiScaleY);

            Marshal.StructureToPtr(info, lParam, true);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Troca a seção visível.
    /// <para>
    /// A transição é fade mais uma subida de seis pixels. <b>Sem deslize horizontal</b>:
    /// deslize sugere que as seções são páginas de um carrossel, e elas não são — são
    /// destinos independentes.
    /// </para>
    /// </summary>
    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }

        ContentScroll.ScrollToTop();

        if (index < 0 || index >= _pages.Length)
        {
            return;
        }

        UIElement page = _pages[index];
        var move = new TranslateTransform(0, 6);
        page.RenderTransform = move;

        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        move.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            });

        if (ReferenceEquals(page, PageHotkeys))
        {
            RefreshHotkeyWarning();
        }
    }

    /// <summary>
    /// Mostra, na própria seção de atalhos, qual atalho não foi aceito e por quê.
    /// <para>
    /// É o lugar onde a informação serve: descobrir pelo log que o atalho está em
    /// conflito não ajuda quem está justamente na tela de configurar atalhos.
    /// </para>
    /// </summary>
    private void RefreshHotkeyWarning()
    {
        IReadOnlyList<HotkeyRegistration> failures = _hotkeys.Failures;

        if (failures.Count == 0)
        {
            HotkeyWarning.Visibility = Visibility.Collapsed;
            return;
        }

        HotkeyWarningText.Text = string.Join(
            Environment.NewLine,
            failures.Select(f => f.Error ?? $"O atalho {f.Text} não foi aceito."));

        HotkeyWarning.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Lista o que a limpeza removeria, sem remover nada.
    /// </summary>
    private void SimulateCleanup()
    {
        IReadOnlyList<CleanupCandidate> candidates = _cleanup.Preview();
        CleanupPreview.Visibility = Visibility.Visible;

        if (candidates.Count == 0)
        {
            CleanupPreviewText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Secondary");
            CleanupPreviewText.Text = "Nada seria removido com as regras atuais.";
            return;
        }

        double megabytes = candidates.Sum(c => c.SizeBytes) / 1024.0 / 1024.0;

        CleanupPreviewText.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Warning");
        CleanupPreviewText.Text = string.Join(
            Environment.NewLine,
            new[] { $"{candidates.Count} arquivo(s), {megabytes:0.0} MB:" }
                .Concat(candidates.Take(12).Select(c => $"  {Path.GetFileName(c.Path)}  ({c.Reason})"))
                .Concat(candidates.Count > 12 ? [$"  e mais {candidates.Count - 12}…"] : []));
    }

    /// <summary>
    /// Abre o seletor de pasta do Windows.
    /// </summary>
    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Onde salvar as capturas",
            InitialDirectory = _model.Folder,
        };

        if (dialog.ShowDialog(this) == true)
        {
            _model.Folder = dialog.FolderName;
        }
    }

    // ---- Estruturas do WM_GETMINMAXINFO ----

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT Reserved;
        public POINT MaxSize;
        public POINT MaxPosition;
        public POINT MinTrackSize;
        public POINT MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT WorkArea;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
