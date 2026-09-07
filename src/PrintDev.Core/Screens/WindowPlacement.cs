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
}
