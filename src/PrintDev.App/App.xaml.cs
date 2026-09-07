using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PrintDev.Bootstrap;
using PrintDev.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Runtime;
using PrintDev.Core.Startup;
using PrintDev.Settings;
using PrintDev.Theme;
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
            // Estes comandos existem para o proprio programa se relancar ELEVADO uma vez
            // so, criar ou remover a tarefa do Agendador, e sair. Nao sobem a bandeja.
            RunTaskCommand(Options.Command);
            return;
        }

        _services = ServiceRegistration.Build(Options, paths, _log);

        // As configuracoes sobem antes da bandeja: o menu ja depende delas, e o nivel
        // de log configurado precisa valer para o resto da inicializacao.
        var settings = _services.GetRequiredService<ISettingsService>();
        settings.Load();
        LoggingSetup.ApplyLevel(settings.Current.Advanced.LogLevel, _log);
        settings.Changed += (_, current) => LoggingSetup.ApplyLevel(current.Advanced.LogLevel, _log);

        StartTheme(settings);

        // Programa movido de pasta deixa a entrada de inicializacao apontando para o
        // nada, e o usuario so descobre no proximo logon.
        _services.GetRequiredService<AutoStartService>().RepairIfMoved();
        _services.GetRequiredService<TrayIconHost>().Show();

        StartHotkeys(settings);

        if (Options.OpenSettings)
        {
            _services.GetRequiredService<SettingsWindowHost>().Show();
        }

        _instanceGuard.WhenActivationRequested(OnActivationRequested);

        _log.Information("Print Dev pronto");
    }

    /// <summary>
    /// Executa um comando de tarefa agendada e encerra.
    /// </summary>
    private void RunTaskCommand(StartupCommand command)
    {
        var autoStart = new AutoStartService(_log);

        bool ok = command == StartupCommand.InstallElevatedTask
            ? autoStart.InstallElevatedTask()
            : autoStart.RemoveElevatedTask();

        Shutdown(ok ? 0 : 3);
    }

    /// <summary>
    /// Aplica o tema e passa a acompanhar o do Windows.
    /// </summary>
    private void StartTheme(ISettingsService settings)
    {
        var theme = _services!.GetRequiredService<ThemeService>();
        theme.Apply(settings.Current.General.Theme);

        // O WPF nao avisa quando nao acha uma fonte embutida: cai na de reserva em
        // silencio. A conferencia deixa isso no log em vez de virar um "esta estranho"
        // sem explicacao semanas depois.
        theme.VerifyFonts();

        // A janela de mensagens que ja existe para os atalhos tambem recebe o aviso de
        // mudanca de cor do sistema. O programa pode nao ter janela nenhuma visivel
        // quando o usuario troca o tema do Windows.
        var messageWindow = _services.GetRequiredService<HotkeyMessageWindow>();
        messageWindow.SystemColorsChanged += (_, _) =>
            Dispatcher.BeginInvoke(theme.OnSystemColorsChanged);

        settings.Changed += (_, current) =>
            Dispatcher.BeginInvoke(() => theme.Apply(current.General.Theme));
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
        _log.Debug("Atalho acionado: {Acao}", action);
        _services!.GetRequiredService<CaptureCoordinator>().Execute(action);
    }

    /// <summary>
    /// Outra instância tentou abrir. O retorno de chamada vem de um thread do pool,
    /// então tudo aqui precisa passar pelo dispatcher.
    /// </summary>
    private void OnActivationRequested()
    {
        Dispatcher.BeginInvoke(() =>
        {
            // Abrir o programa de novo tem que fazer ALGUMA coisa visivel: o usuario
            // clicou esperando ver o programa, nao um nada silencioso.
            _log.Information("Outra instância pediu ativação; abrindo as configurações");
            _services?.GetRequiredService<SettingsWindowHost>().Show();
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
