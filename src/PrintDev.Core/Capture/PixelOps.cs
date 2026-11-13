using System.Numerics;
using System.Runtime.InteropServices;
using PrintDev.Core.Screens;

namespace PrintDev.Core.Capture;

/// <summary>
/// Operações sobre pixels BGRA de cima para baixo, sem preenchimento entre linhas.
/// <para>
/// Ficam aqui, sobre <see cref="Span{T}"/>, para servir tanto aos bytes de uma
/// <see cref="CapturedImage"/> quanto à memória nativa da captura do GDI — a tela
/// congelada do seletor nunca chega a virar array gerenciado.
/// </para>
/// </summary>
internal static class PixelOps
{
    /// <summary>Alfa 255 lido como inteiro de 32 bits em BGRA little-endian.</summary>
    private const uint OpaqueAlpha = 0xFF000000;

    /// <summary>
    /// Força alfa 255 em todos os pixels, de vários em vários por instrução.
    /// <para>
    /// Roda em toda captura, inclusive na tela inteira congelada ao abrir o seletor,
    /// então está no caminho entre apertar a tecla e ver a tela escurecer.
    /// </para>
    /// </summary>
    public static void ForceOpaque(Span<byte> pixels)
    {
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(pixels);
        int index = 0;

        if (Vector.IsHardwareAccelerated && words.Length >= Vector<uint>.Count)
        {
            var mask = new Vector<uint>(OpaqueAlpha);
            int last = words.Length - Vector<uint>.Count;

            for (; index <= last; index += Vector<uint>.Count)
            {
                Span<uint> slice = words.Slice(index, Vector<uint>.Count);
                (new Vector<uint>(slice) | mask).CopyTo(slice);
            }
        }

        for (; index < words.Length; index++)
        {
            words[index] |= OpaqueAlpha;
        }
    }

    /// <summary>
    /// Preenche com uma cor opaca a parte de <paramref name="region"/> que cai dentro de
    /// <paramref name="bounds"/>.
    /// </summary>
    /// <param name="pixels">Os pixels de toda a área <paramref name="bounds"/>.</param>
    /// <param name="bounds">Onde os pixels estão, no desktop virtual.</param>
    /// <param name="region">O que preencher, no desktop virtual.</param>
    public static void Fill(Span<byte> pixels, PixelRect bounds, PixelRect region, byte blue, byte green, byte red)
    {
        PixelRect clipped = region.Intersect(bounds);
        if (clipped.IsEmpty)
        {
            return;
        }

        Span<uint> words = MemoryMarshal.Cast<byte, uint>(pixels);
        uint color = OpaqueAlpha | ((uint)red << 16) | ((uint)green << 8) | blue;

        for (int row = 0; row < clipped.Height; row++)
        {
            int start = ((clipped.Top - bounds.Top + row) * bounds.Width) + (clipped.Left - bounds.Left);
            words.Slice(start, clipped.Width).Fill(color);
        }
    }
}
