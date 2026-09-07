using System.Runtime.InteropServices;
using PrintDev.Core.Screens;

namespace PrintDev.Core.Interop;

/// <summary>Retângulo do Win32, com bordas direita e inferior exclusivas.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly PixelRect ToPixelRect() => PixelRect.FromBounds(Left, Top, Right, Bottom);
}

/// <summary>Ponto do Win32.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X;
    public int Y;
}

/// <summary>Informações de um monitor, com o nome do dispositivo.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEX
{
    /// <summary>Tamanho da estrutura. O Windows recusa a chamada se estiver errado.</summary>
    public int Size;

    public RECT Monitor;
    public RECT WorkArea;
    public uint Flags;

    /// <summary>32 caracteres é o tamanho fixo exigido por <c>CCHDEVICENAME</c>.</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
}

/// <summary>
/// Cabeçalho de um bitmap independente de dispositivo.
/// <para>
/// É a mesma estrutura usada para criar a seção de bitmap na captura e para publicar o
/// formato <c>CF_DIB</c> na área de transferência — e é por isso que a captura entrega
/// os pixels crus em vez de um objeto de imagem já embrulhado.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPINFOHEADER
{
    public uint Size;
    public int Width;

    /// <summary>
    /// Positivo desenha de baixo para cima; negativo, de cima para baixo.
    /// Internamente usamos negativo (mais natural de percorrer); na área de
    /// transferência, positivo, porque programas antigos lidam mal com o contrário.
    /// </summary>
    public int Height;

    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;

    /// <summary>Cria um cabeçalho de 32 bits por pixel, sem compressão.</summary>
    public static BITMAPINFOHEADER Create(int width, int height, bool topDown)
        => new()
        {
            Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            Width = width,
            Height = topDown ? -height : height,
            Planes = 1,
            BitCount = 32,
            Compression = 0, // BI_RGB
            SizeImage = (uint)(width * height * 4),
        };
}
