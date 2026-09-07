using System.Runtime.InteropServices;

namespace PrintDev.Core.Interop;

/// <summary>
/// Assinaturas usadas para enumerar monitores e capturar a tela.
/// </summary>
internal static partial class NativeMethods
{
    // ---- Métricas do desktop virtual (em pixels físicos sob PerMonitorV2) ----
    internal const int SM_XVIRTUALSCREEN = 76;
    internal const int SM_YVIRTUALSCREEN = 77;
    internal const int SM_CXVIRTUALSCREEN = 78;
    internal const int SM_CYVIRTUALSCREEN = 79;

    /// <summary>Sinalizador de monitor principal em <see cref="MONITORINFOEX.Flags"/>.</summary>
    internal const uint MONITORINFOF_PRIMARY = 1;

    /// <summary>Devolve o monitor mais próximo quando o ponto não está em nenhum.</summary>
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>PPP efetivo, o que o usuário escolheu nas configurações de vídeo.</summary>
    internal const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>Cópia direta de pixels, sem combinação.</summary>
    internal const uint SRCCOPY = 0x00CC0020;

    /// <summary>Cores do bitmap dadas diretamente, sem paleta.</summary>
    internal const uint DIB_RGB_COLORS = 0;

    /// <summary>Retângulo real da janela, sem a sombra desenhada pelo compositor.</summary>
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    /// <summary>Janela existe mas está oculta (comum em aplicativos da Store).</summary>
    internal const int DWMWA_CLOAKED = 14;

    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT clip, IntPtr data);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    internal static extern int GetWindowText(IntPtr window, [Out] char[] text, int maxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr window, IntPtr hdc);

    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    // ---- GDI ----

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BITMAPINFOHEADER header,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteDC(IntPtr hdc);
}
