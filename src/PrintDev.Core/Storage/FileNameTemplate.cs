using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace PrintDev.Core.Storage;

/// <summary>
/// Dados que alimentam o modelo de nome de arquivo.
/// </summary>
/// <param name="When">Momento da captura.</param>
/// <param name="AppName">Programa que estava em primeiro plano.</param>
/// <param name="WindowTitle">Título da janela em primeiro plano.</param>
/// <param name="MonitorName">Nome amigável do monitor.</param>
/// <param name="Width">Largura da captura.</param>
/// <param name="Height">Altura da captura.</param>
/// <param name="Mode">Modo usado, por exemplo <c>regiao</c>.</param>
/// <param name="Counter">Contador de capturas da sessão.</param>
public sealed record FileNameContext(
    DateTime When,
    string? AppName = null,
    string? WindowTitle = null,
    string? MonitorName = null,
    int Width = 0,
    int Height = 0,
    string Mode = "",
    int Counter = 0);

/// <summary>
/// Monta o nome do arquivo a partir do modelo configurado.
/// <para>
/// Os nomes dos marcadores são em português porque quem edita o modelo é o dono da
/// máquina, direto no arquivo de configurações.
/// </para>
/// </summary>
public static class FileNameTemplate
{
    /// <summary>Modelo usado quando o configurado está vazio ou só produz lixo.</summary>
    public const string Fallback = "PrintDev_{ano}-{mes}-{dia}_{hora}{min}{seg}";

    /// <summary>
    /// Limite do nome. Bem abaixo dos 255 do sistema de arquivos, para sobrar espaço
    /// para a pasta, a extensão e o sufixo de desempate.
    /// </summary>
    private const int MaxNameLength = 120;

    /// <summary>
    /// Nomes que o Windows reserva para dispositivos. Um arquivo chamado <c>CON.png</c>
    /// não pode ser criado, e o erro que aparece não ajuda em nada.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Aplica o modelo e devolve um nome de arquivo válido, sem extensão.
    /// </summary>
    public static string Render(string? template, FileNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string effective = string.IsNullOrWhiteSpace(template) ? Fallback : template;
        var builder = new StringBuilder(effective.Length + 32);
        int index = 0;

        while (index < effective.Length)
        {
            int open = effective.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(effective, index, effective.Length - index);
                break;
            }

            int close = effective.IndexOf('}', open + 1);
            if (close < 0)
            {
                // Chave sem fechamento: o resto e texto literal. Melhor do que estourar
                // e deixar o usuario sem captura por causa de um erro de digitacao.
                builder.Append(effective, index, effective.Length - index);
                break;
            }

            builder.Append(effective, index, open - index);
            string token = effective[(open + 1)..close];
            builder.Append(Resolve(token, context));
            index = close + 1;
        }

        string name = Sanitize(builder.ToString());
        return name.Length == 0 ? Sanitize(Render(Fallback, context)) : name;
    }

    private static string Resolve(string token, FileNameContext context) => token.ToLowerInvariant() switch
    {
        // InvariantCulture em tudo que e numero: com a cultura tailandesa ou arabe, o
        // ano sairia em outro calendario e os arquivos do usuario ficariam ilegiveis.
        "ano" => context.When.ToString("yyyy", CultureInfo.InvariantCulture),
        "mes" => context.When.ToString("MM", CultureInfo.InvariantCulture),
        "dia" => context.When.ToString("dd", CultureInfo.InvariantCulture),
        "hora" => context.When.ToString("HH", CultureInfo.InvariantCulture),
        "min" => context.When.ToString("mm", CultureInfo.InvariantCulture),
        "seg" => context.When.ToString("ss", CultureInfo.InvariantCulture),
        "ms" => context.When.ToString("fff", CultureInfo.InvariantCulture),
        "contador" => context.Counter.ToString(CultureInfo.InvariantCulture),
        "largura" => context.Width.ToString(CultureInfo.InvariantCulture),
        "altura" => context.Height.ToString(CultureInfo.InvariantCulture),
        "modo" => context.Mode,
        "app" => context.AppName ?? string.Empty,
        "titulo" => context.WindowTitle ?? string.Empty,
        "monitor" => context.MonitorName ?? string.Empty,

        // Marcador desconhecido volta como estava escrito. Some-lo em silencio faria o
        // usuario achar que o modelo funcionou.
        _ => "{" + token + "}",
    };

    /// <summary>
    /// Troca o que o sistema de arquivos não aceita, junta espaços repetidos e corta no
    /// comprimento máximo.
    /// </summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        char[] invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        bool lastWasSpace = false;

        foreach (char character in name)
        {
            char safe = Array.IndexOf(invalid, character) >= 0 ? '_' : character;

            if (safe == ' ')
            {
                if (lastWasSpace)
                {
                    continue;
                }

                lastWasSpace = true;
            }
            else
            {
                lastWasSpace = false;
            }

            builder.Append(safe);
        }

        // Ponto e espaco no fim sao removidos pelo Windows em silencio, o que faria o
        // caminho que devolvemos nao bater com o arquivo que existe no disco.
        string result = builder.ToString().Trim().TrimEnd('.', ' ');

        if (result.Length > MaxNameLength)
        {
            result = result[..MaxNameLength].TrimEnd('.', ' ');
        }

        if (ReservedNames.Contains(result))
        {
            result += "_";
        }

        return result;
    }

    /// <summary>
    /// Monta uma expressão que reconhece os arquivos gerados por um modelo.
    /// <para>
    /// É a trava de segurança da limpeza automática. Sem ela, apontar a pasta de capturas
    /// para uma pasta que já tem coisa do usuário e ligar a limpeza apagaria arquivos que
    /// o Print Dev nunca criou.
    /// </para>
    /// <para>
    /// Reconhece também o sufixo de desempate (<c>_2</c>, <c>_3</c>) e o carimbo de
    /// milissegundos, porque são nomes que o próprio programa produz.
    /// </para>
    /// </summary>
    public static Regex BuildPattern(string? template)
    {
        string effective = string.IsNullOrWhiteSpace(template) ? Fallback : template;
        var pattern = new StringBuilder("^");
        int index = 0;

        while (index < effective.Length)
        {
            int open = effective.IndexOf('{', index);
            if (open < 0)
            {
                pattern.Append(Regex.Escape(Sanitize(effective[index..])));
                break;
            }

            int close = effective.IndexOf('}', open + 1);
            if (close < 0)
            {
                pattern.Append(Regex.Escape(Sanitize(effective[index..])));
                break;
            }

            pattern.Append(Regex.Escape(Sanitize(effective[index..open])));
            pattern.Append(PatternFor(effective[(open + 1)..close]));
            index = close + 1;
        }

        // Sufixo de desempate e extensao.
        pattern.Append(@"(_\d{1,3})?\.(png|jpg|jpeg)$");

        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string PatternFor(string token) => token.ToLowerInvariant() switch
    {
        "ano" => @"\d{4}",
        "mes" or "dia" or "hora" or "min" or "seg" => @"\d{2}",
        "ms" => @"\d{3}",
        "contador" or "largura" or "altura" => @"\d+",

        // Texto livre: o titulo da janela pode ter praticamente qualquer coisa depois de
        // saneado. Nao casa com barra, para a expressao nunca atravessar pasta.
        "app" or "titulo" or "monitor" or "modo" => @"[^\\/]*",

        // Marcador desconhecido virou texto literal na geracao; aqui tambem.
        _ => Regex.Escape("{" + token + "}"),
    };
}
