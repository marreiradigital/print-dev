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
    /// Quem publicou o conteúdo atual.
    /// <para>
    /// É como se sabe se o que está na área de transferência ainda é nosso. Limpar sem
    /// conferir isso apagaria o que a pessoa copiou de outro programa no meio do caminho.
    /// </para>
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardOwner();

    /// <summary>
    /// Publica um formato. Em caso de sucesso, a <b>propriedade do bloco de memória
    /// passa para o sistema</b> e liberá-lo aqui corromperia a área de transferência.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    /// <summary>Lê o bloco de um formato. Só vale com a área aberta.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint format);

    /// <summary>
    /// A janela que está com a área de transferência aberta agora.
    /// <para>
    /// Durante um pedido de renderização sob demanda, é a janela de quem pediu — é
    /// assim que se descobre para qual programa o texto vai.
    /// </para>
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetOpenClipboardWindow();

    /// <summary>Tamanho de um bloco de memória global.</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr GlobalSize(IntPtr memory);

    /// <summary>Alguém pediu um formato anunciado sem os dados: hora de renderizá-lo.</summary>
    internal const int WM_RENDERFORMAT = 0x0305;

    /// <summary>A área de transferência foi esvaziada: o conteúdo deixou de ser nosso.</summary>
    internal const int WM_DESTROYCLIPBOARD = 0x0307;

    /// <summary>Mensagem própria: republicar depois de entregar o caminho.</summary>
    internal const int WM_APP_REARM_CLIPBOARD = 0x8000 + 0x51;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    internal static extern uint RegisterClipboardFormat(string name);

    /// <summary>Mensagens enviadas por outra thread ou processo, como <c>WM_RENDERFORMAT</c>.</summary>
    internal const uint QS_SENDMESSAGE = 0x0040;

    /// <summary>Olhar a fila sem tirar nada dela.</summary>
    internal const uint PM_NOREMOVE = 0x0000;

    /// <summary>Com <see cref="PeekMessage"/>: atender só as mensagens enviadas.</summary>
    internal const uint PM_QS_SENDMESSAGE = QS_SENDMESSAGE << 16;

    /// <summary>Acordar também pelo que já estava esperando antes da chamada.</summary>
    internal const uint MWMO_INPUTAVAILABLE = 0x0004;

    /// <summary>
    /// Espera por mensagens da thread. Com <see cref="QS_SENDMESSAGE"/>, acorda quando outro
    /// programa manda uma mensagem e fica esperando a resposta.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);

    /// <summary>
    /// Olha a fila. Antes de olhar, o Windows entrega as mensagens enviadas que estão
    /// esperando — é isso que se usa aqui, e não a fila em si.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out MSG message, IntPtr window, uint filterMin, uint filterMax, uint remove);

    /// <summary>A janela em primeiro plano mudou.</summary>
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    /// <summary>O aviso chega pela fila desta thread, sem injetar nada em outro processo.</summary>
    internal const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    internal delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint threadId,
        uint time);

    /// <summary>
    /// Pede avisos de eventos da interface — aqui, a troca da janela em primeiro plano.
    /// O delegado precisa ficar vivo enquanto o gancho existir.
    /// </summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(IntPtr hook);

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

/// <summary>Uma mensagem da fila. Só serve aqui como destino do <c>PeekMessage</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr Window;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public int PointX;
    public int PointY;
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
