using System.IO;
using System.Reflection;
using System.Text;
using PrintDev.Core.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PrintDev.Bootstrap;

/// <summary>
/// Configura o log em arquivo.
/// <para>
/// O Print Dev roda sem console e quase sempre sem janela: quando algo dá errado com
/// o atalho global ou com a área de transferência, o arquivo de log é a única
/// evidência que sobra. Por isso ele é ligado antes de qualquer outra coisa.
/// </para>
/// </summary>
public static class LoggingSetup
{
    /// <summary>
    /// Nível mínimo, trocável em tempo de execução. Mudar "Nível de log" nas
    /// configurações passa a valer na hora, sem reiniciar o programa.
    /// </summary>
    public static LoggingLevelSwitch LevelSwitch { get; } = new(LogEventLevel.Information);

    /// <summary>Cria o logger raiz do aplicativo.</summary>
    public static ILogger Create(IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "desconhecida";

        return new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.WithProperty("Versao", version)
            .WriteTo.File(
                path: Path.Combine(paths.LogsDirectory, "printdev-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                // UTF-8 sem BOM: o arquivo e lido por editor de texto e por olho humano,
                // e o texto das mensagens tem acento.
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }
}
