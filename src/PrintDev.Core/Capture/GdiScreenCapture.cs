using System.Runtime.InteropServices;
using PrintDev.Core.Interop;
using PrintDev.Core.Screens;

namespace PrintDev.Core.Capture;

/// <summary>
/// Captura a tela com GDI.
/// <para>
/// Para um quadro único e instantâneo, <c>BitBlt</c> ganha com folga de
/// <c>Windows.Graphics.Capture</c> e da duplicação de área de trabalho do DXGI: nada de
/// assíncrono, nada de Direct3D, nada de moldura amarela de "esta tela está sendo
/// capturada", e nenhuma inicialização. As outras duas só compensam para vídeo.
/// </para>
/// <para>
/// A escolha de <c>CreateDIBSection</c> em vez de um bitmap comum é o que devolve o
/// ponteiro dos pixels — exatamente o que os construtores de <c>CF_DIB</c> e do PNG
/// precisam, sem travar e destravar a imagem depois.
/// </para>
/// </summary>
public static class GdiScreenCapture
{
    /// <summary>
    /// Captura um retângulo do desktop virtual, em pixels físicos.
    /// </summary>
    /// <exception cref="ArgumentException">Retângulo vazio.</exception>
    /// <exception cref="InvalidOperationException">O Windows recusou alguma etapa.</exception>
    public static CapturedImage CaptureRect(PixelRect region)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("Não dá para capturar uma área vazia.", nameof(region));
        }

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousBitmap = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("Não consegui obter o contexto de vídeo da tela.");
            }

            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("Não consegui criar o contexto de vídeo em memória.");
            }

            // topDown: internamente e mais natural percorrer de cima para baixo. A
            // inversao exigida pela area de transferencia acontece la, na hora.
            BITMAPINFOHEADER header = BITMAPINFOHEADER.Create(region.Width, region.Height, topDown: true);

            bitmap = NativeMethods.CreateDIBSection(
                screenDc,
                ref header,
                NativeMethods.DIB_RGB_COLORS,
                out IntPtr bits,
                IntPtr.Zero,
                0);

            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("Não consegui reservar memória para a captura.");
            }

            previousBitmap = NativeMethods.SelectObject(memoryDc, bitmap);

            // CAPTUREBLT (que incluiria janelas em camada) fica de fora: com o
            // compositor do Windows ligado ele raramente muda o resultado e provoca
            // cintilacao visivel na tela.
            if (!NativeMethods.BitBlt(
                    memoryDc, 0, 0, region.Width, region.Height,
                    screenDc, region.Left, region.Top,
                    NativeMethods.SRCCOPY))
            {
                throw new InvalidOperationException("O Windows recusou a cópia da tela.");
            }

            var pixels = new byte[region.Width * region.Height * CapturedImage.BytesPerPixel];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            ForceOpaque(pixels);

            return new CapturedImage(pixels, region);
        }
        finally
        {
            // Vazar identificador de GDI e o tipo de defeito que so aparece depois de
            // algumas centenas de capturas, quando o processo esbarra no limite e a
            // sessao inteira do usuario comeca a falhar. Por isso a limpeza cobre todos
            // os caminhos, inclusive os de excecao.
            if (previousBitmap != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previousBitmap);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>
    /// Captura todos os monitores num único quadro, apagando os buracos do desktop
    /// virtual.
    /// <para>
    /// Uma cópia só do retângulo que envolve tudo é preferível a uma cópia por monitor:
    /// sai atômica, sem risco de os monitores ficarem defasados entre si. O preço é que
    /// as áreas que não pertencem a monitor nenhum vêm com lixo, e é isso que a limpeza
    /// abaixo resolve.
    /// </para>
    /// </summary>
    public static CapturedImage CaptureAllMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        PixelRect box = VirtualDesktop.BoundingBoxOf(monitors);
        if (box.IsEmpty)
        {
            box = VirtualDesktop.BoundingBox();
        }

        CapturedImage image = CaptureRect(box);

        foreach (PixelRect dead in VirtualDesktop.DeadZones(monitors))
        {
            image.Fill(dead);
        }

        return image;
    }

    /// <summary>
    /// Força alfa 255 em todos os pixels.
    /// <para>
    /// <b>É a linha que decide se colar a imagem funciona.</b> O <c>BitBlt</c> grava
    /// zero no byte de alfa, e um zero ali significa "totalmente transparente". Ao
    /// publicar isso como <c>CF_DIBV5</c>, que tem canal alfa de verdade, todo programa
    /// que respeita o canal mostra a captura como um retângulo preto ou como nada.
    /// </para>
    /// <para>
    /// Captura de tela é opaca por natureza, então não há o que preservar: com alfa 255
    /// o pré-multiplicado é idêntico ao direto, e os três formatos publicados na área de
    /// transferência ficam consistentes entre si.
    /// </para>
    /// </summary>
    private static void ForceOpaque(byte[] pixels)
    {
        for (int index = 3; index < pixels.Length; index += CapturedImage.BytesPerPixel)
        {
            pixels[index] = 255;
        }
    }
}
