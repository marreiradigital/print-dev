namespace PrintDev.Core.Hotkeys;

/// <summary>Modificadores aceitos por <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,

    /// <summary>
    /// Impede que segurar a tecla dispare o atalho repetidamente.
    /// Sempre ligado: segurar PrtSc não pode abrir dez seletores.
    /// </summary>
    NoRepeat = 0x4000,
}

/// <summary>
/// Um atalho global já interpretado: modificadores mais a tecla virtual.
/// </summary>
/// <param name="Modifiers">Modificadores, sem o <see cref="HotkeyModifiers.NoRepeat"/>.</param>
/// <param name="VirtualKey">Código de tecla virtual do Windows.</param>
public readonly record struct HotkeyDefinition(HotkeyModifiers Modifiers, uint VirtualKey)
{
    /// <summary>Atalho vazio, que não deve ser registrado.</summary>
    public static HotkeyDefinition None { get; } = new(HotkeyModifiers.None, 0);

    /// <summary>Indica se há uma tecla de verdade para registrar.</summary>
    public bool IsSet => VirtualKey != 0;

    /// <summary>
    /// Modificadores como o <c>RegisterHotKey</c> espera, com o
    /// <see cref="HotkeyModifiers.NoRepeat"/> acrescentado.
    /// </summary>
    public uint ModifiersForRegistration => (uint)(Modifiers | HotkeyModifiers.NoRepeat);

    /// <inheritdoc/>
    public override string ToString() => HotkeyParser.Format(this);
}
