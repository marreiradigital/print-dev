using Microsoft.Extensions.DependencyInjection;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Startup;
using PrintDev.Tray;
using Serilog;

namespace PrintDev.Bootstrap;

/// <summary>
/// Composition root. É o único lugar do aplicativo que sabe montar o grafo de
/// dependências — e é o que garante que o <c>Dispose</c> de todo serviço aconteça na
/// ordem certa quando o programa encerra.
/// </summary>
public static class ServiceRegistration
{
    /// <summary>Monta o provedor de serviços do aplicativo.</summary>
    public static ServiceProvider Build(StartupOptions options, IAppPaths paths, ILogger logger)
    {
        var services = new ServiceCollection();

        services.AddSingleton(options);
        services.AddSingleton(paths);
        services.AddSingleton(logger);

        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<TrayIconHost>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Falha cedo e alto: dependencia faltando vira erro na inicializacao,
            // e nao um NullReferenceException seis telas depois.
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
