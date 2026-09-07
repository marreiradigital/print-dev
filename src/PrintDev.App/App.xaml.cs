using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PrintDev.Bootstrap;
using PrintDev.Core.Configuration;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Runtime;
using PrintDev.Core.Startup;
using PrintDev.Tray;
using Serilog;

namespace PrintDev;

/// <summary>
/// Ponto de entrada do Print Dev.
/// <para>
/// A ordem aqui é deliberada: instância única antes de tudo (para a segunda instância
/// morrer barato), log logo em seguida (para que qualquer falha do restante apareça em
/// algum lugar), e só então o container e a bandeja.
/// </para>
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _instanceGuard;
    private ServiceProvider? _services;
    private ILogger _log = Serilog.Core.Logger.None;

    /// <summary>Argumentos de linha de comando já interpretados.</summary>
    public StartupOptions Options { get; private set; } = StartupOptions.Default;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Options = StartupOptions.Parse(e.Args);

        _instanceGuard = SingleInstanceGuard.Acquire();
        if (!_instanceGuard.IsPrimary)
        {
            // Ja existe um Print Dev nesta sessao. Avisa o primeiro e sai sem escrever
            // log nem tocar em arquivo: duas instancias mexendo no mesmo settings.json
            // e a receita para perder configuracao.
            _instanceGuard.SignalPrimary();
            Shutdown(0);
            return;
        }

        var paths = new AppPaths();
        paths.EnsureCreated();

        _log = LoggingSetup.Create(paths);
        Log.Logger = _log;
        GlobalExceptionHandlers.Install(this, _log);

        _log.Information(
            "Print Dev iniciando. Silencioso={Silencioso} Configuracoes={Configuracoes} Comando={Comando}",
            Options.Silent,
            Options.OpenSettings,
            Options.Command);

        if (Options.Unrecognized.Count > 0)
        {
            _log.Warning("Argumentos ignorados: {Argumentos}", Options.Unrecognized);
        }

        if (Options.Command != StartupCommand.Run)
        {
            // As tarefas do Agendador entram junto com a tela de configuracoes, que e
            // onde o usuario liga a inicializacao elevada. Ate la, o comando e recusado
            // de forma visivel no log em vez de fingir que funcionou.
            _log.Warning("O comando {Comando} ainda não está implementado.", Options.Command);
            Shutdown(2);
            return;
        }

        _services = ServiceRegistration.Build(Options, paths, _log);

        // As configuracoes sobem antes da bandeja: o menu ja depende delas, e o nivel
        // de log configurado precisa valer para o resto da inicializacao.
        var settings = _services.GetRequiredService<ISettingsService>();
        settings.Load();
        LoggingSetup.ApplyLevel(settings.Current.Advanced.LogLevel, _log);
        settings.Changed += (_, current) => LoggingSetup.ApplyLevel(current.Advanced.LogLevel, _log);

        _services.GetRequiredService<TrayIconHost>().Show();

        StartHotkeys(settings);

        _instanceGuard.WhenActivationRequested(OnActivationRequested);

        _log.Information("Print Dev pronto");
    }

    /// <summary>
    /// Registra os atalhos globais e liga o vigia que os reavê quando outro programa
    /// toma a tecla.
    /// </summary>
    private void StartHotkeys(ISettingsService settings)
    {
        var hotkeys = _services!.GetRequiredService<HotkeyManager>();
        hotkeys.Triggered += (_, action) => Dispatcher.BeginInvoke(() => OnHotkey(action));
        hotkeys.Apply(settings.Current.Hotkeys);

        var guardian = _services.GetRequiredService<HotkeyGuardian>();
        guardian.Start();

        // Atalho trocado no arquivo de configuracoes vale na hora, sem reiniciar.
        settings.Changed += (_, current) => Dispatcher.BeginInvoke(() =>
        {
            hotkeys.Apply(current.Hotkeys);
            guardian.Start();
        });
    }

    private void OnHotkey(HotkeyAction action)
    {
        // TODO(fase 3b): ligar na captura de tela. Ate la, o registro no log ja prova
        // que a tecla chegou ao programa, que e o que este commit entrega.
        _log.Information("Atalho acionado: {Acao}", action);
    }

    /// <summary>
    /// Outra instância tentou abrir. O retorno de chamada vem de um thread do pool,
    /// então tudo aqui precisa passar pelo dispatcher.
    /// </summary>
    private void OnActivationRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            // TODO(fase 8): trazer o painel de configuracoes para a frente, que e o que
            // o usuario espera ao abrir o programa de novo.
            _log.Information("Outra instância pediu ativação");
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // A ordem inversa da inicializacao: o container primeiro (para o icone sair da
        // bandeja), a instancia unica depois, e o log por ultimo, para registrar tudo.
        _services?.Dispose();
        _instanceGuard?.Dispose();

        _log.Information("Print Dev encerrado com código {Codigo}", e.ApplicationExitCode);
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
