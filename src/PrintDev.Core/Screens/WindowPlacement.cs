using System.Runtime.InteropServices;
using PrintDev.Core.Interop;

namespace PrintDev.Core.Screens;

/// <summary>
/// Posiciona janelas em pixels físicos.
/// <para>
/// Existe porque as propriedades de posição e tamanho do WPF são unidades independentes
/// de dispositivo: o mesmo número significa uma quantidade de pixels diferente em cada
/// monitor, conforme a escala. Uma janela que precisa cobrir um monitor <b>exatamente</b>
/// — como as do seletor — tem de ser posicionada por fora, com os números reais.
/// </para>
/// </summary>
public static class WindowPlacement
{
    /// <summary>
    /// Põe a janela sobre o retângulo indicado, acima das demais, sem roubar o foco.
    /// </summary>
    public static bool PlaceOnTop(IntPtr window, PixelRect bounds)
    {
        if (window == IntPtr.Zero || bounds.IsEmpty)
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            window,
            NativeMethods.HWND_TOPMOST,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Traz a janela para o primeiro plano.
    /// <para>
    /// O Windows costuma recusar isso para evitar que programas roubem o foco, mas
    /// aceita logo após o processo ter recebido um atalho global — que é exatamente a
    /// situação em que o seletor abre.
    /// </para>
    /// </summary>
    public static bool BringToFront(IntPtr window)
        => window != IntPtr.Zero && NativeMethods.SetForegroundWindow(window);

    /// <summary>
    /// Trava o tamanho e a posição de uma janela maximizada na área de trabalho do
    /// monitor em que ela está, respondendo a <c>WM_GETMINMAXINFO</c>.
    /// <para>
    /// Resolve DOIS defeitos com uma solução só. Sem isto, uma janela sem moldura nativa,
    /// ao maximizar, cobre a barra de tarefas <b>e</b> sangra oito pixels para fora da
    /// tela em cada lado — porque o Windows a posiciona em (-8, -8) contando com a
    /// moldura que aqui não existe.
    /// </para>
    /// </summary>
    /// <param name="minimumWidth">
    /// Largura mínima em <b>pixels físicos</b>. O Windows não escala este valor: passá-lo
    /// em unidades independentes de dispositivo daria um mínimo errado em monitor com
    /// escala diferente de 100%.
    /// </param>
    /// <param name="minimumHeight">Altura mínima, em pixels físicos.</param>
    /// <returns>
    /// <see langword="true"/> quando os limites foram escritos — e só então a mensagem
    /// pode ser dada como tratada.
    /// </returns>
    public static bool ClampMaximizeToWorkArea(
        IntPtr window,
        IntPtr minMaxInfo,
        int minimumWidth,
        int minimumHeight)
    {
        if (window == IntPtr.Zero || minMaxInfo == IntPtr.Zero)
        {
            return false;
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var info = new MONITORINFOEX { Size = Marshal.SizeOf<MONITORINFOEX>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var limits = Marshal.PtrToStructure<MINMAXINFO>(minMaxInfo);

        limits.MaxPosition.X = info.WorkArea.Left - info.Monitor.Left;
        limits.MaxPosition.Y = info.WorkArea.Top - info.Monitor.Top;
        limits.MaxSize.X = info.WorkArea.Right - info.WorkArea.Left;
        limits.MaxSize.Y = info.WorkArea.Bottom - info.WorkArea.Top;
        limits.MinTrackSize.X = minimumWidth;
        limits.MinTrackSize.Y = minimumHeight;

        Marshal.StructureToPtr(limits, minMaxInfo, fDeleteOld: true);
        return true;
    }

    /// <summary>
    /// Pede ao compositor do Windows para arredondar os cantos da janela.
    /// <para>
    /// E a alternativa a ligar transparencia por camada, que arredondaria por conta
    /// propria ao custo de desligar a aceleracao por hardware daquela janela. No
    /// Windows 10 nao ha efeito, e os cantos ficam retos - o que e aceitavel.
    /// </para>
    /// </summary>
    public static void RoundCorners(IntPtr window, bool small = false)
    {
        if (window == IntPtr.Zero || Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        int preference = small ? NativeMethods.DWMWCP_ROUNDSMALL : NativeMethods.DWMWCP_ROUND;

        try
        {
            NativeMethods.DwmSetWindowAttribute(
                window,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                sizeof(int));
        }
        catch (Exception)
        {
            // Enfeite: em versao antiga do compositor a chamada simplesmente falha, e a
            // janela fica com canto reto. Nada aqui justifica derrubar o programa.
        }
    }
}
