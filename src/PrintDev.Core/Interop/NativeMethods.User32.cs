using System.Runtime.InteropServices;

namespace PrintDev.Core.Interop;

/// <summary>
/// Assinaturas do <c>user32.dll</c>.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>Mensagem entregue quando um atalho global é acionado.</summary>
    internal const int WM_HOTKEY = 0x0312;

    /// <summary>O atalho já pertence a outro programa.</summary>
    internal const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    /// <summary>
    /// Registra um atalho global.
    /// </summary>
    /// <param name="hWnd">Janela que receberá <see cref="WM_HOTKEY"/>.</param>
    /// <param name="id">Identificador do atalho, entre 0x0000 e 0xBFFF.</param>
    /// <param name="fsModifiers">Modificadores, com MOD_NOREPEAT.</param>
    /// <param name="vk">Código de tecla virtual.</param>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>Libera um atalho registrado por <see cref="RegisterHotKey"/>.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
