using System.Runtime.InteropServices;

namespace PrintDev.Core.Interop;

/// <summary>
/// Envio de teclas sintéticas, usado pelos atalhos de colagem forçada.
/// </summary>
internal static partial class NativeMethods
{
    internal const byte VK_CONTROL = 0x11;
    internal const byte VK_SHIFT = 0x10;
    internal const byte VK_MENU = 0x12; // Alt
    internal const byte VK_V = 0x56;

    internal const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Bit alto ligado significa tecla pressionada agora.</summary>
    internal const int KeyPressedMask = 0x8000;

    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);
}
