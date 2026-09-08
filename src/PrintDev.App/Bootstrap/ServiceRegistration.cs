using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using PrintDev.Capture;
using PrintDev.Core.Capture;
using PrintDev.Core.Clipboard;
using PrintDev.Core.Cloud;
using PrintDev.Core.Configuration;
using PrintDev.Core.History;
using PrintDev.Core.Hotkeys;
using PrintDev.Notifications;
using PrintDev.Overlay;
using PrintDev.Settings;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Maintenance;
using PrintDev.Core.Ocr;
using PrintDev.Core.Runtime;
using PrintDev.Core.Startup;
using PrintDev.Core.Updates;
using PrintDev.Theme;
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

        // A janela de mensagens precisa nascer na thread de interface: e ela que
        // tem o laco de mensagens onde o WM_HOTKEY chega.
        services.AddSingleton<HotkeyMessageWindow>();
        services.AddSingleton<HotkeyManager>();
        services.AddSingleton<HotkeyGuardian>();

        services.AddSingleton<AutoStartService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<SettingsWindowHost>();
        services.AddSingleton<ClipboardWriter>();
        services.AddSingleton<CaptureHistory>();
        services.AddSingleton<CaptureUndoService>();
        services.AddSingleton<ToastHost>();
        services.AddSingleton<OverlayCoordinator>();
        services.AddSingleton<WindowsOcrService>();
        services.AddSingleton<CleanupService>();
        services.AddSingleton<CapturePipeline>();
        services.AddSingleton<CaptureCoordinator>();
        services.AddSingleton<TrayIconHost>();

        // Um HttpClient para o programa inteiro. Criar um por uso esgota as portas
        // efemeras do Windows; um estatico eterno nao percebe mudanca de DNS.
        services.AddSingleton(_ => CriarClienteHttp());
        services.AddSingleton<GitHubReleaseClient>();
        services.AddSingleton<UpdateInstaller>();
        services.AddSingleton<UpdateStateStore>();
        services.AddSingleton<UpdateService>();

        services.AddSingleton<CloudLinkStore>();
        services.AddSingleton<CloudUploader>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            // Falha cedo e alto: dependencia faltando vira erro na inicializacao,
            // e nao um NullReferenceException seis telas depois.
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// O cliente HTTP do programa.
    /// <para>
    /// Sem tempo-limite proprio: ele atende tanto a consulta de metadados quanto o
    /// download do instalador, e um teto que serve para uma mata a outra. Cada
    /// chamador impoe o seu com um token de cancelamento.
    /// </para>
    /// </summary>
    private static HttpClient CriarClienteHttp()
    {
        var transporte = new SocketsHttpHandler
        {
            // Sem isto a conexao guardada nunca reavalia o DNS, e o programa fica
            // falando com um endereco que mudou ha horas.
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var cliente = new HttpClient(transporte)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        // A API do GitHub recusa requisicao sem User-Agent, com 403 e sem explicar.
        cliente.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("PrintDev", ReleaseTag.Current.ToString(3)));
        cliente.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("(+https://printdev.marreira.dev)"));

        cliente.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        cliente.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return cliente;
    }
}
