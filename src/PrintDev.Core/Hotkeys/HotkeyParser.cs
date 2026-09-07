using System.Diagnostics.CodeAnalysis;

namespace PrintDev.Core.Hotkeys;

/// <summary>
/// Converte um atalho entre o texto do arquivo de configurações e a forma que o
/// Windows entende.
/// <para>
/// Lógica pura e testada porque é o ponto em que um erro fica invisível: um atalho
/// escrito errado no JSON simplesmente não registra, e o usuário só descobre apertando
/// a tecla e não vendo nada acontecer.
/// </para>
/// </summary>
public static class HotkeyParser
{
    /// <summary>Nome canônico de cada tecla, para ida e volta sem perda.</summary>
    private static readonly Dictionary<uint, string> NamesByKey = new()
    {
        [0x2C] = "PrtSc",
        [0x08] = "Backspace",
        [0x09] = "Tab",
        [0x0D] = "Enter",
        [0x13] = "Pause",
        [0x14] = "CapsLock",
        [0x1B] = "Esc",
        [0x20] = "Espaco",
        [0x21] = "PageUp",
        [0x22] = "PageDown",
        [0x23] = "End",
        [0x24] = "Home",
        [0x25] = "Esquerda",
        [0x26] = "Cima",
        [0x27] = "Direita",
        [0x28] = "Baixo",
        [0x2D] = "Insert",
        [0x2E] = "Delete",
        [0xBC] = "Virgula",
        [0xBE] = "Ponto",
        [0xBD] = "Menos",
        [0xBB] = "Mais",
    };

    /// <summary>Aceita mais de uma grafia por tecla, para o arquivo perdoar quem digita.</summary>
    private static readonly Dictionary<string, uint> KeysByName = BuildKeyLookup();

    private static readonly Dictionary<string, HotkeyModifiers> ModifiersByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = HotkeyModifiers.Control,
        ["control"] = HotkeyModifiers.Control,
        ["controle"] = HotkeyModifiers.Control,
        ["alt"] = HotkeyModifiers.Alt,
        ["shift"] = HotkeyModifiers.Shift,
        ["win"] = HotkeyModifiers.Windows,
        ["windows"] = HotkeyModifiers.Windows,
        ["super"] = HotkeyModifiers.Windows,
        ["meta"] = HotkeyModifiers.Windows,
    };

    /// <summary>
    /// Combinações que o Windows reserva e não entrega a aplicação nenhuma. Recusar
    /// aqui, com motivo, é melhor do que deixar o usuário configurar algo que nunca vai
    /// funcionar e culpar o programa.
    /// </summary>
    private static readonly (HotkeyModifiers Modifiers, uint Key, string Reason)[] Reserved =
    [
        (HotkeyModifiers.Windows, 0x4C, "Win+L bloqueia a estação de trabalho."),
        (HotkeyModifiers.Windows | HotkeyModifiers.Shift, 0x53, "Win+Shift+S é da Ferramenta de Captura do Windows."),
        (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x2E, "Ctrl+Alt+Del é do sistema."),
        (HotkeyModifiers.Windows, 0x09, "Win+Tab é da visão de tarefas."),
    ];

    /// <summary>
    /// Interpreta um atalho escrito como <c>Ctrl+Shift+PrtSc</c>.
    /// </summary>
    /// <param name="text">Texto do atalho. Vazio devolve <see cref="HotkeyDefinition.None"/>.</param>
    /// <param name="hotkey">Atalho interpretado.</param>
    /// <param name="error">Motivo da recusa, em português, pronto para a interface.</param>
    public static bool TryParse(
        string? text,
        out HotkeyDefinition hotkey,
        [NotNullWhen(false)] out string? error)
    {
        hotkey = HotkeyDefinition.None;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            // Atalho em branco e uma escolha valida: significa "sem atalho".
            return true;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        uint virtualKey = 0;

        foreach (string rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            if (ModifiersByName.TryGetValue(part, out HotkeyModifiers modifier))
            {
                modifiers |= modifier;
                continue;
            }

            if (virtualKey != 0)
            {
                error = $"O atalho tem mais de uma tecla principal: {text}";
                return false;
            }

            if (!KeysByName.TryGetValue(part, out virtualKey))
            {
                error = $"Tecla desconhecida no atalho: {part}";
                return false;
            }
        }

        if (virtualKey == 0)
        {
            error = $"O atalho não tem tecla principal: {text}";
            return false;
        }

        foreach ((HotkeyModifiers reservedModifiers, uint reservedKey, string reason) in Reserved)
        {
            if (modifiers == reservedModifiers && virtualKey == reservedKey)
            {
                error = reason;
                return false;
            }
        }

        hotkey = new HotkeyDefinition(modifiers, virtualKey);
        return true;
    }

    /// <summary>
    /// Escreve o atalho de volta em texto, na ordem canônica Ctrl, Alt, Shift, Win.
    /// A ordem fixa é o que garante que ler e escrever devolva sempre a mesma string.
    /// </summary>
    public static string Format(HotkeyDefinition hotkey)
    {
        if (!hotkey.IsSet)
        {
            return string.Empty;
        }

        var parts = new List<string>(4);

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(NameOf(hotkey.VirtualKey));
        return string.Join('+', parts);
    }

    /// <summary>Nome legível de uma tecla virtual.</summary>
    public static string NameOf(uint virtualKey)
    {
        if (NamesByKey.TryGetValue(virtualKey, out string? name))
        {
            return name;
        }

        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return "F" + (virtualKey - 0x6F);
        }

        return "0x" + virtualKey.ToString("X2");
    }

    private static Dictionary<string, uint> BuildKeyLookup()
    {
        var lookup = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        foreach ((uint key, string name) in NamesByKey)
        {
            lookup[name] = key;
        }

        // Grafias alternativas: o arquivo e editado a mao e ninguem decora o nome exato.
        lookup["PrintScreen"] = 0x2C;
        lookup["Print"] = 0x2C;
        lookup["Snapshot"] = 0x2C;
        lookup["Escape"] = 0x1B;
        lookup["Space"] = 0x20;
        lookup["Return"] = 0x0D;
        lookup["Del"] = 0x2E;
        lookup["Ins"] = 0x2D;
        lookup["PgUp"] = 0x21;
        lookup["PgDn"] = 0x22;
        lookup["Left"] = 0x25;
        lookup["Up"] = 0x26;
        lookup["Right"] = 0x27;
        lookup["Down"] = 0x28;

        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            lookup[letter.ToString()] = letter;
        }

        for (char digit = '0'; digit <= '9'; digit++)
        {
            lookup[digit.ToString()] = digit;
        }

        for (uint index = 1; index <= 24; index++)
        {
            lookup["F" + index] = 0x6F + index;
        }

        return lookup;
    }
}
