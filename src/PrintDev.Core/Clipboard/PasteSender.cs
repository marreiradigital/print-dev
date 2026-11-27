using PrintDev.Core.Interop;

namespace PrintDev.Core.Clipboard;

/// <summary>
/// Envia um <c>Ctrl+V</c> sintético para a janela em primeiro plano.
/// <para>
/// É o que faz os atalhos de colagem forçada terem efeito: o usuário aperta
/// <c>Ctrl+Shift+V</c>, o Print Dev troca o conteúdo da área de transferência para o
/// formato pedido e manda o colar — em vez de o usuário ter que apertar duas
/// combinações seguidas.
/// </para>
/// </summary>
public static class PasteSender
{
    /// <summary>Quanto esperar a pessoa soltar as teclas do atalho antes de colar.</summary>
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromMilliseconds(600);

    /// <summary>
    /// Manda colar assim que a pessoa soltar Shift, Alt e Windows do atalho.
    /// </summary>
    /// <remarks>
    /// A parte delicada é que o usuário <b>ainda está segurando</b> as teclas do atalho
    /// quando isto roda. Se mandássemos o V direto, o programa de destino veria
    /// <c>Ctrl+Shift+V</c> — que em muitos editores é "colar sem formatação" e em
    /// terminais não é colar nada — ou <c>Win+V</c>, que abre o histórico.
    /// <para>
    /// Antes, os modificadores presos eram soltos à força e <b>apertados de novo</b>
    /// depois. Apertar o Alt de novo, com a pessoa soltando o dela logo em seguida, é um
    /// toque de Alt sozinho: o programa de destino abria o menu e engolia o que se
    /// digitava depois. Esperar a pessoa soltar evita mexer no estado das teclas dela;
    /// só se ela continuar segurando além do tempo é que os modificadores são soltos, e
    /// nunca reapertados.
    /// </para>
    /// </remarks>
    public static async Task SendPasteAsync()
    {
        long deadline = Environment.TickCount64 + (long)ReleaseWait.TotalMilliseconds;

        while (AnyModifierHeld() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(15).ConfigureAwait(true);
        }

        SendPaste();
    }

    private static void SendPaste()
    {
        foreach (byte key in (byte[])[NativeMethods.VK_SHIFT, NativeMethods.VK_MENU, NativeMethods.VK_LWIN, NativeMethods.VK_RWIN])
        {
            if (IsDown(key))
            {
                Release(key);
            }
        }

        bool controlDown = IsDown(NativeMethods.VK_CONTROL);

        if (!controlDown)
        {
            Press(NativeMethods.VK_CONTROL);
        }

        Press(NativeMethods.VK_V);
        Release(NativeMethods.VK_V);

        if (!controlDown)
        {
            Release(NativeMethods.VK_CONTROL);
        }
    }

    private static bool AnyModifierHeld()
        => IsDown(NativeMethods.VK_SHIFT)
           || IsDown(NativeMethods.VK_MENU)
           || IsDown(NativeMethods.VK_LWIN)
           || IsDown(NativeMethods.VK_RWIN);

    private static bool IsDown(byte virtualKey)
        => (NativeMethods.GetAsyncKeyState(virtualKey) & NativeMethods.KeyPressedMask) != 0;

    private static void Press(byte virtualKey)
        => NativeMethods.keybd_event(virtualKey, 0, 0, UIntPtr.Zero);

    private static void Release(byte virtualKey)
        => NativeMethods.keybd_event(virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
}
