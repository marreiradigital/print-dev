namespace PrintDev.Core.Startup;

/// <summary>
/// Argumentos de linha de comando já interpretados.
/// <para>
/// Lógica pura de propósito: é o primeiro ponto do app que precisa de teste,
/// porque um argumento mal lido no autostart faz o programa subir do jeito errado
/// todo dia sem ninguém perceber.
/// </para>
/// </summary>
public sealed record StartupOptions
{
    /// <summary>Padrão de execução: janela nenhuma, só a bandeja.</summary>
    public static StartupOptions Default { get; } = new();

    /// <summary>
    /// Sobe direto para a bandeja, sem abrir as configurações nem mostrar boas-vindas.
    /// É o que o autostart usa.
    /// </summary>
    public bool Silent { get; init; }

    /// <summary>Abre o painel de configurações assim que o app subir.</summary>
    public bool OpenSettings { get; init; }

    /// <summary>Tarefa pontual a executar em vez da execução normal.</summary>
    public StartupCommand Command { get; init; } = StartupCommand.Run;

    /// <summary>
    /// Argumentos não reconhecidos, preservados para irem ao log.
    /// Argumento desconhecido nunca derruba o app: o Windows pode passar coisas
    /// que não são nossas (associação de arquivo, relançamento do shell).
    /// </summary>
    public IReadOnlyList<string> Unrecognized { get; init; } = [];

    /// <summary>
    /// Interpreta os argumentos. Aceita as três convenções que aparecem no Windows
    /// (<c>--nome</c>, <c>-n</c> e <c>/nome</c>), sem diferenciar maiúsculas.
    /// </summary>
    public static StartupOptions Parse(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return Default;
        }

        bool silent = false;
        bool openSettings = false;
        StartupCommand command = StartupCommand.Run;
        List<string>? unrecognized = null;

        foreach (string raw in args)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            switch (Normalize(raw))
            {
                case "silencioso" or "silent" or "s" or "tray":
                    silent = true;
                    break;

                case "configuracoes" or "settings" or "c":
                    openSettings = true;
                    break;

                case "install-elevated-task" or "instalar-tarefa-elevada":
                    command = StartupCommand.InstallElevatedTask;
                    break;

                case "uninstall-elevated-task" or "remover-tarefa-elevada":
                    command = StartupCommand.UninstallElevatedTask;
                    break;

                default:
                    (unrecognized ??= []).Add(raw);
                    break;
            }
        }

        return new StartupOptions
        {
            Silent = silent,
            OpenSettings = openSettings,
            Command = command,
            Unrecognized = unrecognized is null ? [] : unrecognized.AsReadOnly(),
        };
    }

    /// <summary>
    /// Tira o prefixo (<c>--</c>, <c>-</c> ou <c>/</c>) e normaliza para minúsculas
    /// invariantes. O <c>ToLowerInvariant</c> é proposital: com a cultura turca,
    /// <c>ToLower</c> transformaria "I" em "ı" e o argumento deixaria de casar.
    /// </summary>
    private static string Normalize(string argument)
    {
        ReadOnlySpan<char> span = argument.AsSpan().Trim();

        if (span.StartsWith("--"))
        {
            span = span[2..];
        }
        else if (span.Length > 0 && (span[0] == '-' || span[0] == '/'))
        {
            span = span[1..];
        }

        return span.ToString().ToLowerInvariant();
    }
}
