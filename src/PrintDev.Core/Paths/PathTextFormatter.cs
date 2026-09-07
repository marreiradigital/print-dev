using System.IO;
using PrintDev.Core.Configuration;

namespace PrintDev.Core.Paths;

/// <summary>
/// Escreve o caminho do arquivo do jeito que o destino espera.
/// <para>
/// É a metade textual do produto. Quando a captura é colada num campo que só aceita
/// texto — terminal, editor de código, campo de formulário —, é este texto que chega
/// lá. Ele precisa ser <b>colável sem correção manual</b>: caminho com espaço num
/// terminal sem aspas não funciona, e caminho do Windows num terminal WSL também não.
/// </para>
/// </summary>
public static class PathTextFormatter
{
    /// <summary>
    /// Formata o caminho conforme as configurações da área de transferência.
    /// </summary>
    public static string Format(string fullPath, ClipboardSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(settings);

        return Format(fullPath, settings.PathFormat, settings.Quotes, settings.WslRoot,
            settings.MarkdownTemplate, settings.HtmlTemplate);
    }

    /// <summary>Formata o caminho com cada opção passada explicitamente.</summary>
    public static string Format(
        string fullPath,
        PathTextFormat format,
        QuoteMode quotes = QuoteMode.QuandoTiverEspaco,
        string wslRoot = "/mnt/",
        string markdownTemplate = "![{nome}]({caminho})",
        string htmlTemplate = "<img src={aspa}{caminho}{aspa} alt={aspa}{nome}{aspa}>")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        string path = fullPath.Trim();
        string name = Path.GetFileNameWithoutExtension(path);

        return format switch
        {
            PathTextFormat.BarraNormal => Quote(path.Replace('\\', '/'), quotes),
            PathTextFormat.Wsl => Quote(ToWsl(path, wslRoot), quotes),
            PathTextFormat.UriFile => ToFileUri(path),
            PathTextFormat.Markdown => Fill(markdownTemplate, name, ToFileUri(path)),
            PathTextFormat.Html => Fill(htmlTemplate, name, ToFileUri(path)),
            PathTextFormat.SomenteNome => Quote(Path.GetFileName(path), quotes),
            _ => Quote(path, quotes),
        };
    }

    /// <summary>
    /// Converte para o caminho que um terminal WSL entende:
    /// <c>C:\Users\x</c> vira <c>/mnt/c/Users/x</c>.
    /// </summary>
    /// <param name="root">
    /// Raiz de montagem. <c>/mnt/</c> no WSL; <c>/</c> no Git Bash, que monta as
    /// unidades direto na raiz.
    /// </param>
    public static string ToWsl(string fullPath, string root = "/mnt/")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        string path = fullPath.Trim();

        // Caminho de rede nao tem traducao: no WSL ele e alcancado por ponto de
        // montagem, que so a maquina de destino conhece. Devolver o formato do Windows
        // e mais honesto do que inventar um caminho que nao existe.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.Length < 2 || path[1] != ':' || !char.IsLetter(path[0]))
        {
            return path.Replace('\\', '/');
        }

        string prefix = string.IsNullOrEmpty(root) ? "/mnt/" : root;
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        string rest = path[2..].Replace('\\', '/').TrimStart('/');
        return prefix + char.ToLowerInvariant(path[0]) + "/" + rest;
    }

    /// <summary>
    /// Converte para o endereço com o esquema de arquivo, com os espaços codificados.
    /// </summary>
    public static string ToFileUri(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        try
        {
            return new Uri(fullPath.Trim()).AbsoluteUri;
        }
        catch (UriFormatException)
        {
            // Caminho estranho demais para virar endereco. Melhor devolver o texto cru
            // do que perder a colagem inteira.
            return fullPath.Trim();
        }
    }

    /// <summary>
    /// Envolve em aspas conforme a política.
    /// <para>
    /// O padrão é só quando há espaço, porque é exatamente o caso em que o terminal
    /// quebra. Aspas sempre atrapalhariam a colagem em campo de formulário.
    /// </para>
    /// </summary>
    public static string Quote(string text, QuoteMode mode) => mode switch
    {
        QuoteMode.Sempre => Wrap(text),
        QuoteMode.QuandoTiverEspaco when text.Contains(' ', StringComparison.Ordinal) => Wrap(text),
        _ => text,
    };

    private static string Wrap(string text)
        => text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text : "\"" + text + "\"";

    /// <summary>
    /// Preenche um modelo. O marcador <c>{aspa}</c> existe para o modelo poder produzir
    /// aspas sem que o arquivo JSON precise escapá-las, o que o tornaria ilegível para
    /// quem edita à mão.
    /// </summary>
    private static string Fill(string template, string name, string path)
        => (string.IsNullOrWhiteSpace(template) ? "{caminho}" : template)
            .Replace("{nome}", name, StringComparison.Ordinal)
            .Replace("{caminho}", path, StringComparison.Ordinal)
            .Replace("{aspa}", "\"", StringComparison.Ordinal);
}
