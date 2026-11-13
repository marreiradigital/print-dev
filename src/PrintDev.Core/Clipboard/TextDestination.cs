using PrintDev.Core.Configuration;

namespace PrintDev.Core.Clipboard;

/// <summary>
/// Quais programas recebem o caminho do arquivo quando a captura é colada.
/// <para>
/// O caminho só serve onde não dá para colar imagem: terminal, IDE, editor de código. Em
/// todo o resto — chat, documento, Win+V, navegador — quem cola tem de receber a imagem.
/// Publicar os dois juntos e deixar o destino escolher não funcionava: o histórico do
/// Win+V e vários programas que aceitam imagem preferem o texto quando ele existe, e
/// colavam o caminho no lugar da captura.
/// </para>
/// </summary>
public static class TextDestination
{
    /// <summary>
    /// A lista que vem de fábrica: terminais, shells, IDEs e editores de código. Nome do
    /// processo, sem <c>.exe</c>, sem diferenciar maiúsculas.
    /// <para>
    /// Editores de texto puro (Bloco de Notas, Notepad++) entram também: eles não
    /// aceitam imagem, e sem o caminho a colagem neles não entregaria nada.
    /// </para>
    /// </summary>
    public static NameList Defaults { get; } =
    [
        // Terminais e shells.
        "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "powershell_ise",
        "wezterm-gui", "alacritty", "mintty", "Hyper", "Tabby", "Warp", "ConEmu", "ConEmu64",
        "kitty", "putty", "MobaXterm", "Termius", "FluentTerminal.App",

        // IDEs e editores de código.
        "Code", "Code - Insiders", "VSCodium", "Cursor", "Windsurf", "Trae", "Kiro", "Antigravity",
        "Zed", "devenv", "rider64", "idea64", "pycharm64", "webstorm64", "phpstorm64", "goland64",
        "clion64", "rustrover64", "datagrip64", "rubymine64", "studio64", "fleet",
        "sublime_text", "notepad++", "Notepad", "nvim-qt", "neovide", "gvim", "emacs", "runemacs",
    ];

    /// <summary>
    /// Verdadeiro quando o processo está na lista.
    /// </summary>
    /// <param name="processName">Nome do processo que pediu o texto, com ou sem <c>.exe</c>.</param>
    /// <param name="destinations">A lista configurada.</param>
    public static bool Accepts(string? processName, IEnumerable<string> destinations)
    {
        ArgumentNullException.ThrowIfNull(destinations);

        string wanted = Normalize(processName);
        if (wanted.Length == 0)
        {
            return false;
        }

        foreach (string candidate in destinations)
        {
            if (string.Equals(Normalize(candidate), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Lê a lista escrita à mão — separada por vírgula, ponto e vírgula ou quebra de linha
    /// — sem repetições nem itens vazios.
    /// </summary>
    public static NameList Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string piece in text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string name = Normalize(piece);
            if (name.Length > 0 && seen.Add(name))
            {
                result.Add(name);
            }
        }

        return new NameList(result);
    }

    /// <summary>Tira espaços nas pontas e a extensão <c>.exe</c>, se houver.</summary>
    public static string Normalize(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim().Trim('"');

        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4].TrimEnd()
            : trimmed;
    }
}
