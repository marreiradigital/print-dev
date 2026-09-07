using PrintDev.Core.Interop;

namespace PrintDev.Core.Screens;

/// <summary>
/// O conjunto de monitores, em pixels físicos.
/// </summary>
public static class VirtualDesktop
{
    /// <summary>
    /// Lista os monitores ligados agora.
    /// <para>
    /// Sob PerMonitorV2 os retângulos já vêm em pixels físicos, sem virtualização — é
    /// o que permite tratar dois monitores com escalas diferentes no mesmo sistema de
    /// coordenadas.
    /// </para>
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>(2);

        NativeMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEX { Size = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };

                if (NativeMethods.GetMonitorInfo(handle, ref info))
                {
                    monitors.Add(Describe(handle, info));
                }

                return true;
            },
            IntPtr.Zero);

        return monitors;
    }

    private static MonitorInfo Describe(IntPtr handle, MONITORINFOEX info)
    {
        // GetDpiForMonitor pode falhar em maquina virtual ou driver antigo. Cair para
        // 96 PPP e o certo: 96 significa 100%, que e o comportamento sem escala.
        uint dpiX = 96;
        uint dpiY = 96;

        if (NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out uint x, out uint y) == 0)
        {
            dpiX = x == 0 ? 96 : x;
            dpiY = y == 0 ? 96 : y;
        }

        return new MonitorInfo(
            handle,
            info.DeviceName,
            info.Monitor.ToPixelRect(),
            info.WorkArea.ToPixelRect(),
            dpiX,
            dpiY,
            (info.Flags & NativeMethods.MONITORINFOF_PRIMARY) != 0);
    }

    /// <summary>Posição atual do cursor, em pixels físicos.</summary>
    public static (int X, int Y) CursorPosition()
        => NativeMethods.GetCursorPos(out POINT point) ? (point.X, point.Y) : (0, 0);

    /// <summary>
    /// Retângulo que envolve todos os monitores, lido direto do Windows.
    /// </summary>
    public static PixelRect BoundingBox()
        => new(
            NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
            NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Retângulo que envolve os monitores dados. Separado da versão que consulta o
    /// Windows para poder ser testado com topologias de mentira.
    /// </summary>
    public static PixelRect BoundingBoxOf(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        PixelRect box = PixelRect.Empty;
        foreach (MonitorInfo monitor in monitors)
        {
            box = box.Union(monitor.Bounds);
        }

        return box;
    }

    /// <summary>
    /// Monitor que contém o ponto, ou <see langword="null"/> quando o ponto cai num
    /// buraco do desktop virtual.
    /// <para>
    /// Buraco não é hipótese acadêmica: dois monitores de alturas diferentes, ou
    /// desalinhados na vertical, deixam áreas dentro do retângulo que envolve tudo mas
    /// fora de qualquer tela. Nesta máquina, por exemplo, existe uma faixa assim abaixo
    /// do monitor secundário.
    /// </para>
    /// </summary>
    public static MonitorInfo? MonitorAt(IReadOnlyList<MonitorInfo> monitors, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        foreach (MonitorInfo monitor in monitors)
        {
            if (monitor.Bounds.Contains(x, y))
            {
                return monitor;
            }
        }

        return null;
    }

    /// <summary>
    /// Monitor que contém o ponto ou, se ele cair num buraco, o mais próximo. Nunca
    /// devolve nulo enquanto houver pelo menos um monitor — é a versão para quando é
    /// preciso escolher uma tela de qualquer jeito.
    /// </summary>
    public static MonitorInfo? NearestMonitor(IReadOnlyList<MonitorInfo> monitors, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        MonitorInfo? exact = MonitorAt(monitors, x, y);
        if (exact is not null || monitors.Count == 0)
        {
            return exact ?? null;
        }

        MonitorInfo? best = null;
        long bestDistance = long.MaxValue;

        foreach (MonitorInfo monitor in monitors)
        {
            // Distancia ao ponto mais proximo do retangulo, ao quadrado: evita a raiz
            // quadrada e o risco de estouro com coordenadas grandes.
            long dx = x < monitor.Bounds.Left ? monitor.Bounds.Left - x
                : x >= monitor.Bounds.Right ? x - monitor.Bounds.Right + 1 : 0;
            long dy = y < monitor.Bounds.Top ? monitor.Bounds.Top - y
                : y >= monitor.Bounds.Bottom ? y - monitor.Bounds.Bottom + 1 : 0;

            long distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = monitor;
            }
        }

        return best;
    }

    /// <summary>Monitor sob o cursor, ou o principal se algo der errado.</summary>
    public static MonitorInfo? MonitorUnderCursor(IReadOnlyList<MonitorInfo> monitors)
    {
        (int x, int y) = CursorPosition();
        return NearestMonitor(monitors, x, y) ?? monitors.FirstOrDefault(m => m.IsPrimary);
    }

    /// <summary>
    /// Áreas que estão dentro do retângulo que envolve tudo mas não pertencem a nenhum
    /// monitor. A captura precisa zerá-las: o que o Windows devolve ali é lixo.
    /// </summary>
    public static IReadOnlyList<PixelRect> DeadZones(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        PixelRect box = BoundingBoxOf(monitors);
        if (box.IsEmpty)
        {
            return [];
        }

        // Varredura por faixas horizontais: as bordas superior e inferior de cada
        // monitor cortam o retangulo em faixas, e dentro de cada faixa o conjunto de
        // monitores presentes nao muda. Bem mais simples que subtracao de poligonos, e
        // suficiente porque monitores sao sempre retangulos alinhados aos eixos.
        var rows = new SortedSet<int> { box.Top, box.Bottom };
        var columns = new SortedSet<int> { box.Left, box.Right };

        foreach (MonitorInfo monitor in monitors)
        {
            rows.Add(monitor.Bounds.Top);
            rows.Add(monitor.Bounds.Bottom);
            columns.Add(monitor.Bounds.Left);
            columns.Add(monitor.Bounds.Right);
        }

        int[] rowEdges = [.. rows];
        int[] columnEdges = [.. columns];
        var dead = new List<PixelRect>();

        for (int row = 0; row < rowEdges.Length - 1; row++)
        {
            for (int column = 0; column < columnEdges.Length - 1; column++)
            {
                var cell = PixelRect.FromBounds(
                    columnEdges[column],
                    rowEdges[row],
                    columnEdges[column + 1],
                    rowEdges[row + 1]);

                if (cell.IsEmpty)
                {
                    continue;
                }

                bool covered = monitors.Any(m => m.Bounds.Contains(cell));
                if (!covered)
                {
                    dead.Add(cell);
                }
            }
        }

        return dead;
    }
}
