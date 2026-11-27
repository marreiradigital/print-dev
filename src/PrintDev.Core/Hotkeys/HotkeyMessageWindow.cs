using System.Windows.Interop;
using PrintDev.Core.Interop;

namespace PrintDev.Core.Hotkeys;

/// <summary>
/// Janela invisível que existe só para receber mensagens do Windows.
/// <para>
/// O <c>RegisterHotKey</c> exige um identificador de janela numa thread com laço de
/// mensagens, e o Print Dev pode passar horas sem nenhuma janela visível. Uma janela
/// <c>HWND_MESSAGE</c> resolve isso: ela não aparece, não entra na barra de tarefas,
/// não recebe foco e custa praticamente nada.
/// </para>
/// <para>
/// Também é a dona da área de transferência, por onde chegam os pedidos do caminho sob
/// demanda. Mensagens de difusão — <c>WM_SETTINGCHANGE</c>, <c>WM_DISPLAYCHANGE</c> —
/// <b>nunca</b> chegam a uma janela <c>HWND_MESSAGE</c>; a troca de tema é acompanhada
/// pelo <c>ThemeService</c>, por <c>SystemEvents</c>.
/// </para>
/// </summary>
public sealed class HotkeyMessageWindow : IDisposable
{
    /// <summary>Valor de <c>HWND_MESSAGE</c>: cria a janela como filha de mensagens.</summary>
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;

    /// <summary>Cria a janela. Precisa ser chamado na thread de interface.</summary>
    public HotkeyMessageWindow()
    {
        var parameters = new HwndSourceParameters("PrintDev.JanelaDeMensagens")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>Identificador da janela, para passar ao <c>RegisterHotKey</c>.</summary>
    public IntPtr Handle => _source?.Handle
        ?? throw new ObjectDisposedException(nameof(HotkeyMessageWindow));

    /// <summary>
    /// Disparado quando um atalho registrado é acionado, com o identificador dele.
    /// </summary>
    public event EventHandler<int>? HotkeyPressed;

    /// <summary>
    /// Um programa pediu um formato que publicamos sem os dados. Quem escuta precisa
    /// responder ali mesmo, antes de o evento voltar: o programa está esperando.
    /// </summary>
    public event EventHandler<uint>? ClipboardFormatRequested;

    /// <summary>A área de transferência foi esvaziada e o conteúdo dela deixou de ser nosso.</summary>
    public event EventHandler? ClipboardEmptied;

    /// <summary>
    /// Hora de republicar o caminho sob demanda, depois de entregá-lo. Leva o número da
    /// publicação a que o pedido se refere.
    /// </summary>
    public event EventHandler<int>? ClipboardRearmRequested;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_HOTKEY:
                handled = true;

                // Nada de trabalho pesado aqui dentro. Enquanto este metodo nao retorna,
                // a fila de mensagens da interface fica parada - e capturar a tela leva
                // dezenas de milissegundos. Quem escuta o evento agenda o trabalho.
                HotkeyPressed?.Invoke(this, wParam.ToInt32());
                break;

            // Esta janela e a dona da area de transferencia: e por ela que o Windows pede
            // os formatos anunciados sem os dados.
            case NativeMethods.WM_RENDERFORMAT:
                handled = true;
                ClipboardFormatRequested?.Invoke(this, unchecked((uint)wParam.ToInt64()));
                break;

            case NativeMethods.WM_DESTROYCLIPBOARD:
                handled = true;
                ClipboardEmptied?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.WM_APP_REARM_CLIPBOARD:
                handled = true;
                ClipboardRearmRequested?.Invoke(this, unchecked((int)wParam.ToInt64()));
                break;
        }

        return IntPtr.Zero;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_source is null)
        {
            return;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
    }
}
