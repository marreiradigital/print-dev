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
    /// <summary>
    /// Manda colar.
    /// </summary>
    /// <remarks>
    /// A parte delicada é que o usuário <b>ainda está segurando</b> as teclas do atalho
    /// quando isto roda. Se mandássemos o V direto, o programa de destino veria
    /// <c>Ctrl+Shift+V</c> — que em muitos editores é "colar sem formatação" e em
    /// terminais não é colar nada. Por isso os modificadores presos são soltos antes, e
    /// devolvidos depois, para o usuário não terminar com uma tecla grudada.
    /// </remarks>
    public static void SendPaste()
    {
        bool shiftDown = IsDown(NativeMethods.VK_SHIFT);
        bool altDown = IsDown(NativeMethods.VK_MENU);
        bool controlDown = IsDown(NativeMethods.VK_CONTROL);

        if (shiftDown)
        {
            Release(NativeMethods.VK_SHIFT);
        }

        if (altDown)
        {
            Release(NativeMethods.VK_MENU);
        }

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

        // Devolve o estado das teclas que estavam presas. Sem isto, o Windows continua
        // achando que o Shift esta pressionado ate o usuario aperta-lo e solta-lo de
        // novo - e tudo que ele digitar sai em maiuscula.
        if (altDown)
        {
            Press(NativeMethods.VK_MENU);
        }

        if (shiftDown)
        {
            Press(NativeMethods.VK_SHIFT);
        }
    }

    private static bool IsDown(byte virtualKey)
        => (NativeMethods.GetAsyncKeyState(virtualKey) & NativeMethods.KeyPressedMask) != 0;

    private static void Press(byte virtualKey)
        => NativeMethods.keybd_event(virtualKey, 0, 0, UIntPtr.Zero);

    private static void Release(byte virtualKey)
        => NativeMethods.keybd_event(virtualKey, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
}
