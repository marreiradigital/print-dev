using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PrintDev.Core.Hotkeys;

namespace PrintDev.Controls;

/// <summary>
/// Campo que grava um atalho apertando as teclas, em vez de pedir que ele seja escrito.
/// <para>
/// Clicar no campo começa a gravação; a primeira combinação completa vira o atalho, na
/// grafia canônica (<c>Ctrl+Shift+PrtSc</c>). <c>Esc</c> desiste e devolve o que havia,
/// <c>Backspace</c> ou <c>Delete</c> deixam a ação sem atalho, e <c>Tab</c> sai do campo
/// como sempre.
/// </para>
/// <para>
/// O texto vai para a configuração só no fim (vínculo <c>Explicit</c>): os estados
/// intermediários — o convite, o <c>Ctrl+Shift+…</c> — nunca chegam ao arquivo.
/// </para>
/// </summary>
public sealed class HotkeyBox : TextBox
{
    /// <summary>Curto de propósito: cabe no campo junto com a moldura de foco.</summary>
    private const string Invitation = "Aperte o atalho…";

    private string _before = string.Empty;
    private bool _recording;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        ToolTip = "Clique e aperte a combinação. Esc desiste, Backspace deixa sem atalho.";
    }

    /// <summary>A gravação começou: quem escuta solta os atalhos globais.</summary>
    public event EventHandler? RecordingStarted;

    /// <summary>A gravação terminou, gravando ou desistindo.</summary>
    public event EventHandler? RecordingEnded;

    /// <inheritdoc/>
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);

        if (_recording)
        {
            return;
        }

        _recording = true;
        _before = Text;
        Text = Invitation;
        RecordingStarted?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);

        if (!_recording)
        {
            return;
        }

        // Saiu sem completar uma combinacao: volta o que havia, sem tocar na configuracao.
        _recording = false;
        Text = _before;
        RecordingEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_recording)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        Key key = RealKey(e);
        HotkeyModifiers modifiers = CurrentModifiers();

        // Tab sem modificador continua navegando: e o jeito de sair do campo pelo teclado.
        if (key == Key.Tab && modifiers == HotkeyModifiers.None)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        if (IsModifier(key))
        {
            Text = HotkeyParser.FormatPartial(modifiers | ModifierOf(key));
            return;
        }

        if (modifiers == HotkeyModifiers.None && key == Key.Escape)
        {
            Finish(commit: null);
            return;
        }

        if (modifiers == HotkeyModifiers.None && key is Key.Back or Key.Delete)
        {
            Finish(commit: string.Empty);
            return;
        }

        // O PrtSc e tratado ao soltar: o Windows quase nunca entrega o apertar dele.
        if (key == Key.Snapshot)
        {
            return;
        }

        Commit(key, modifiers);
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!_recording)
        {
            base.OnPreviewKeyUp(e);
            return;
        }

        Key key = RealKey(e);
        e.Handled = true;

        if (key == Key.Snapshot)
        {
            Commit(key, CurrentModifiers());
            return;
        }

        // Soltou um modificador sem completar: o convite volta a refletir o que sobrou.
        if (IsModifier(key))
        {
            HotkeyModifiers left = CurrentModifiers() & ~ModifierOf(key);
            Text = left == HotkeyModifiers.None ? Invitation : HotkeyParser.FormatPartial(left);
        }
    }

    private void Commit(Key key, HotkeyModifiers modifiers)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return;
        }

        Finish(HotkeyParser.Format(new HotkeyDefinition(modifiers, (uint)virtualKey)));
    }

    /// <summary>
    /// Encerra a gravação. Com texto, ele vira o atalho; com nulo, volta o anterior.
    /// </summary>
    private void Finish(string? commit)
    {
        _recording = false;
        Text = commit ?? _before;

        if (commit is not null)
        {
            GetBindingExpression(TextProperty)?.UpdateSource();
        }

        RecordingEnded?.Invoke(this, EventArgs.Empty);

        // Sair do campo deixa claro que acabou; clicar de novo grava outra vez.
        Keyboard.ClearFocus();
    }

    /// <summary>A tecla de verdade: o WPF esconde atrás de System as combinações com Alt.</summary>
    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    private static HotkeyModifiers CurrentModifiers()
    {
        ModifierKeys pressed = Keyboard.Modifiers;
        HotkeyModifiers modifiers = HotkeyModifiers.None;

        if (pressed.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (pressed.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (pressed.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (pressed.HasFlag(ModifierKeys.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsModifier(Key key) => ModifierOf(key) != HotkeyModifiers.None;

    private static HotkeyModifiers ModifierOf(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => HotkeyModifiers.Control,
        Key.LeftAlt or Key.RightAlt => HotkeyModifiers.Alt,
        Key.LeftShift or Key.RightShift => HotkeyModifiers.Shift,
        Key.LWin or Key.RWin => HotkeyModifiers.Windows,
        _ => HotkeyModifiers.None,
    };
}
