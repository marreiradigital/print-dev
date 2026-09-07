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
/// Ela também será a receptora de <c>WM_SETTINGCHANGE</c> (mudança de tema do Windows)
/// e <c>WM_DISPLAYCHANGE</c> (monitor conectado ou removido), pelo mesmo motivo.
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

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        handled = true;

        // Nada de trabalho pesado aqui dentro. Enquanto este metodo nao retorna, a fila
        // de mensagens da thread de interface fica parada - e capturar a tela leva
        // dezenas de milissegundos. Quem escuta o evento agenda o trabalho.
        HotkeyPressed?.Invoke(this, wParam.ToInt32());
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
