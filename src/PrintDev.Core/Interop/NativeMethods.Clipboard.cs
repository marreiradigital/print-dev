using System.Runtime.InteropServices;

namespace PrintDev.Core.Interop;

/// <summary>
/// Assinaturas da área de transferência e da memória global.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>Bitmap independente de dispositivo, cabeçalho de 40 bytes.</summary>
    internal const uint CF_DIB = 8;

    /// <summary>Texto em UTF-16.</summary>
    internal const uint CF_UNICODETEXT = 13;

    /// <summary>Lista de arquivos, como num arrastar e soltar do Explorador.</summary>
    internal const uint CF_HDROP = 15;

    /// <summary>Bitmap com cabeçalho de 124 bytes, este com canal alfa de verdade.</summary>
    internal const uint CF_DIBV5 = 17;

    /// <summary>Memória móvel: é o que a área de transferência exige.</summary>
    internal const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>Outro processo está com a área de transferência aberta.</summary>
    internal const int ERROR_ACCESS_DENIED = 5;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    /// <summary>
    /// Publica um formato. Em caso de sucesso, a <b>propriedade do bloco de memória
    /// passa para o sistema</b> e liberá-lo aqui corromperia a área de transferência.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    internal static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr memory);
}

/// <summary>
/// Cabeçalho de bitmap versão 5, com máscaras de canal e canal alfa.
/// <para>
/// São exatamente 124 bytes, e o Windows recusa a estrutura se o tamanho não bater.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BITMAPV5HEADER
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;
    public uint RedMask;
    public uint GreenMask;
    public uint BlueMask;
    public uint AlphaMask;
    public uint CSType;

    // CIEXYZTRIPLE: nove inteiros. So faz sentido com perfil de cor proprio, que nao
    // usamos - ficam zerados porque CSType declara sRGB.
    public int EndpointRedX;
    public int EndpointRedY;
    public int EndpointRedZ;
    public int EndpointGreenX;
    public int EndpointGreenY;
    public int EndpointGreenZ;
    public int EndpointBlueX;
    public int EndpointBlueY;
    public int EndpointBlueZ;

    public uint GammaRed;
    public uint GammaGreen;
    public uint GammaBlue;
    public uint Intent;
    public uint ProfileData;
    public uint ProfileSize;
    public uint Reserved;
}

/// <summary>
/// Cabeçalho da lista de arquivos do <c>CF_HDROP</c>. Vinte bytes, seguidos das strings.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DROPFILES
{
    /// <summary>Deslocamento em que a lista de nomes começa. Sempre 20.</summary>
    public uint FilesOffset;

    public int PointX;
    public int PointY;

    /// <summary>Zero: as coordenadas acima são da área de cliente.</summary>
    public int NonClientArea;

    /// <summary>Um: os nomes estão em UTF-16.</summary>
    public int Wide;
}
