using System.Diagnostics;
using PrintDev.Core.Interop;
using PrintDev.Core.Screens;

namespace PrintDev.Core.Capture;

/// <summary>
/// Quem estava em primeiro plano no instante da captura.
/// </summary>
/// <param name="Handle">Identificador da janela.</param>
/// <param name="Title">Título da janela.</param>
/// <param name="ProcessName">Nome do processo, sem extensão.</param>
/// <param name="Bounds">Retângulo real da janela, sem a sombra do compositor.</param>
public sealed record ActiveWindowInfo(IntPtr Handle, string Title, string ProcessName, PixelRect Bounds)
{
    /// <summary>Valor usado quando não há janela em primeiro plano.</summary>
    public static ActiveWindowInfo Unknown { get; } = new(IntPtr.Zero, string.Empty, string.Empty, PixelRect.Empty);

    /// <summary>
    /// Lê a janela em primeiro plano <b>agora</b>.
    /// <para>
    /// Precisa ser chamado antes de o seletor aparecer. Assim que o overlay é exibido,
    /// ele passa a ser a janela em primeiro plano, e o nome que iria para o arquivo
    /// seria o do próprio Print Dev — armadilha fácil de cair e difícil de perceber.
    /// </para>
    /// </summary>
    public static ActiveWindowInfo Current()
    {
        IntPtr window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return Unknown;
        }

        return new ActiveWindowInfo(window, ReadTitle(window), ReadProcessName(window), ReadBounds(window));
    }

    private static string ReadTitle(IntPtr window)
    {
        var buffer = new char[512];
        int length = NativeMethods.GetWindowText(window, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }

    private static string ReadProcessName(IntPtr window)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            // O processo pode ter morrido entre a leitura do identificador e a consulta,
            // ou ser de outro nivel de privilegio. Nome vazio e melhor que excecao.
            return string.Empty;
        }
    }

    private static PixelRect ReadBounds(IntPtr window)
    {
        // O retangulo estendido exclui a sombra que o compositor desenha em volta da
        // janela. Usar GetWindowRect direto deixaria uma borda escura no recorte.
        if (NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT extended,
                System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
        {
            return extended.ToPixelRect();
        }

        return NativeMethods.GetWindowRect(window, out RECT plain) ? plain.ToPixelRect() : PixelRect.Empty;
    }
}
