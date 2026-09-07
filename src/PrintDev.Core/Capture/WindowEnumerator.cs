using System.Runtime.InteropServices;
using PrintDev.Core.Interop;
using PrintDev.Core.Screens;

namespace PrintDev.Core.Capture;

/// <summary>Uma janela candidata à captura no modo Janela.</summary>
/// <param name="Handle">Identificador da janela.</param>
/// <param name="Bounds">Retângulo real, sem a sombra do compositor.</param>
/// <param name="Title">Título, para mostrar no destaque.</param>
public sealed record WindowTarget(IntPtr Handle, PixelRect Bounds, string Title);

/// <summary>
/// Lista as janelas que podem ser capturadas.
/// <para>
/// <b>Precisa rodar antes de o seletor aparecer.</b> Depois que o overlay está na tela,
/// perguntar ao Windows qual janela está sob o cursor devolve sempre o próprio overlay —
/// ele cobre tudo. A lista é fotografada antes e o teste de acerto é feito contra ela.
/// </para>
/// </summary>
public static class WindowEnumerator
{
    /// <summary>Janelas menores que isto são ferramentas e ruído.</summary>
    private const int MinimumSide = 24;

    /// <summary>
    /// Fotografa as janelas visíveis, já na ordem de empilhamento (a da frente primeiro).
    /// </summary>
    public static IReadOnlyList<WindowTarget> Snapshot(PixelRect desktop)
    {
        var targets = new List<WindowTarget>(32);

        NativeMethods.EnumWindows(
            (window, _) =>
            {
                if (IsCapturable(window, desktop, out PixelRect bounds))
                {
                    targets.Add(new WindowTarget(window, bounds, ReadTitle(window)));
                }

                return true;
            },
            IntPtr.Zero);

        return targets;
    }

    /// <summary>
    /// Janela mais à frente que contém o ponto. <see langword="null"/> quando o ponto
    /// está sobre a área de trabalho.
    /// </summary>
    public static WindowTarget? HitTest(IReadOnlyList<WindowTarget> targets, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(targets);

        // A enumeracao ja veio de frente para tras, entao a primeira que contem o ponto
        // e a que o usuario esta vendo.
        foreach (WindowTarget target in targets)
        {
            if (target.Bounds.Contains(x, y))
            {
                return target;
            }
        }

        return null;
    }

    private static bool IsCapturable(IntPtr window, PixelRect desktop, out PixelRect bounds)
    {
        bounds = PixelRect.Empty;

        if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
        {
            return false;
        }

        // So janelas de nivel mais alto: os filhos ja estao dentro do retangulo do pai.
        if (NativeMethods.GetAncestor(window, NativeMethods.GA_ROOT) != window)
        {
            return false;
        }

        int exStyle = NativeMethods.GetWindowLong(window, NativeMethods.GWL_EXSTYLE);
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 || (exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0)
        {
            return false;
        }

        // Janelas "encobertas" sao o fantasma dos aplicativos da Store: existem, estao
        // marcadas como visiveis e nao aparecem na tela. Sem este filtro, o destaque
        // pularia para retangulos que o usuario nao consegue ver.
        if (NativeMethods.DwmGetWindowAttribute(
                window, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return false;
        }

        // O retangulo estendido exclui a sombra que o compositor desenha em volta - sem
        // ele, todo recorte de janela sairia com uma borda escura em volta.
        PixelRect rect = NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT extended,
                Marshal.SizeOf<RECT>()) == 0
            ? extended.ToPixelRect()
            : NativeMethods.GetWindowRect(window, out RECT plain) ? plain.ToPixelRect() : PixelRect.Empty;

        if (rect.Width < MinimumSide || rect.Height < MinimumSide)
        {
            return false;
        }

        // Janela parcialmente fora da tela: so interessa o que da para capturar.
        bounds = rect.Intersect(desktop);
        return !bounds.IsEmpty;
    }

    private static string ReadTitle(IntPtr window)
    {
        var buffer = new char[256];
        int length = NativeMethods.GetWindowText(window, buffer, buffer.Length);
        return length <= 0 ? string.Empty : new string(buffer, 0, length);
    }
}
