using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using PrintDev.Bootstrap;
using PrintDev.Capture;
using PrintDev.Notifications;
using PrintDev.Overlay;
using PrintDev.Core.Clipboard;
using PrintDev.Core.Configuration;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Maintenance;
using PrintDev.Core.Runtime;
using PrintDev.Core.Startup;
using PrintDev.Core.Updates;
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

        if (Options.Command != StartupCommand.Run)
        {
            // Estes comandos existem para o proprio programa se relancar ELEVADO uma vez
            // so, criar ou remover a tarefa do Agendador, e sair. Nao sobem a bandeja.
            //
            // Rodam ANTES da instancia unica, e de proposito: o processo elevado nasce
            // com o Print Dev normal ja de pe (foi ele quem pediu), entao a guarda o
            // trataria como "segunda instancia", acordaria a primaria e sairia sem criar
            // a tarefa. "Iniciar com privilegios elevados" nunca funcionaria.
            StartLogging();
            _log.Information("Print Dev executando o comando {Comando}", Options.Command);
            RunTaskCommand(Options.Command);
            return;
        }

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

        try
        {
            StartPrimary();
        }
        catch (Exception exception)
        {
            OnStartupFailed(exception);
        }
    }

    /// <summary>
    /// A partida da instância primária: log, configurações, bandeja, atalhos.
    /// <para>
    /// Roda dentro de um <c>try</c> em <see cref="OnStartup"/>. Uma exceção aqui chegava ao
    /// tratador do dispatcher, que a engole e deixa o processo vivo — sem bandeja, sem
    /// atalho e segurando o mutex de instância única, o que fazia o próximo duplo clique
    /// só acordar um fantasma.
    /// </para>
    /// </summary>
    private void StartPrimary()
    {
        AppPaths paths = StartLogging();

        _log.Information(
            "Print Dev iniciando. Silencioso={Silencioso} Configuracoes={Configuracoes} Comando={Comando}",
            Options.Silent,
            Options.OpenSettings,
            Options.Command);

        if (Options.Unrecognized.Count > 0)
        {
            _log.Warning("Argumentos ignorados: {Argumentos}", Options.Unrecognized);
        }

        _services = ServiceRegistration.Build(Options, paths, _log);

        // As configuracoes sobem antes da bandeja: o menu ja depende delas, e o nivel
        // de log configurado precisa valer para o resto da inicializacao.
        var settings = _services.GetRequiredService<ISettingsService>();
        settings.Load();
        LoggingSetup.ApplyLevel(settings.Current.Advanced.LogLevel, _log);
        settings.Changed += (_, current) => LoggingSetup.ApplyLevel(current.Advanced.LogLevel, _log);

        StartTheme(_services, settings);

        // O caminho vai para a area de transferencia sem os dados, e e por esta janela
        // que o Windows pede o texto na hora da colagem.
        _services.GetRequiredService<ClipboardWriter>().Attach(_services.GetRequiredService<HotkeyMessageWindow>());

        // Programa movido de pasta deixa a entrada de inicializacao apontando para o
        // nada, e o usuario so descobre no proximo logon.
        _services.GetRequiredService<AutoStartService>().RepairIfMoved();
        _services.GetRequiredService<TrayIconHost>().Show();

        // O aviso pede a anotacao por evento; e aqui que o pedido encontra quem executa.
        var toasts = _services.GetRequiredService<ToastHost>();

        toasts.AnnotateRequested += (_, item) =>
        {
            if (item.Path is not null)
            {
                _services.GetRequiredService<CaptureCoordinator>().AnnotateFile(item.Path);
            }
        };

        toasts.PinRequested += (_, item) =>
        {
            if (item.Path is not null)
            {
                _services.GetRequiredService<CaptureCoordinator>().PinFile(item.Path);
            }
        };

        StartHotkeys(_services, settings);

        if (Options.OpenSettings)
        {
            _services.GetRequiredService<SettingsWindowHost>().Show();
        }

        _instanceGuard?.WhenActivationRequested(OnActivationRequested);

        // A limpeza roda uma vez por inicializacao, em segundo plano: varrer a pasta na
        // thread de interface deixaria o programa mudo por segundos numa pasta grande.
        Task.Run(() =>
        {
            try
            {
                _services.GetRequiredService<CleanupService>().Run();
            }
            catch (Exception exception)
            {
                _log.Error(exception, "Falha na limpeza automática");
            }
        });

        StartUpdates(_services);
        WarmOverlaySoon(_services);

        _log.Information("Print Dev pronto");

        // A inicialização tocou dezenas de megabytes que nunca mais serão lidos: ler o
        // XAML, compilar os métodos na primeira execução, carregar as configurações. Um
        // programa que fica horas parado na bandeja não tem por que segurar isso.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(WorkingSet.ReleaseSoon));
    }

    /// <summary>
    /// A partida falhou no meio: registra o motivo e encerra de verdade, liberando o
    /// mutex (o <see cref="OnExit"/> descarta tudo o que chegou a ser criado).
    /// </summary>
    private void OnStartupFailed(Exception exception)
    {
        _log.Fatal(exception, "O Print Dev não conseguiu iniciar; encerrando");

        // Quem abriu o programa na mao merece saber por que nada apareceu. Na partida
        // silenciosa (logon) um dialogo seria pior que o silencio: o log basta.
        if (!Options.Silent)
        {
            MessageBox.Show(
                $"O Print Dev não conseguiu iniciar.{Environment.NewLine}{Environment.NewLine}{exception.Message}"
                + $"{Environment.NewLine}{Environment.NewLine}O detalhe completo está no arquivo de log.",
                "Print Dev",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }

        Shutdown(1);
    }

    /// <summary>
    /// Aquece o seletor alguns segundos depois de subir, com o programa já quieto: a
    /// primeira abertura do dia deixa de pagar sozinha o custo de primeiro uso.
    /// </summary>
    private static void WarmOverlaySoon(ServiceProvider services)
    {
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(4),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            services.GetRequiredService<OverlayCoordinator>().Warm();
        };

        timer.Start();
    }

    /// <summary>
    /// Liga o log em arquivo e os tratadores de exceção. É o primeiro passo depois de
    /// saber que este processo vai de fato fazer alguma coisa.
    /// </summary>
    private AppPaths StartLogging()
    {
        var paths = new AppPaths();
        paths.EnsureCreated();

        _log = LoggingSetup.Create(paths);
        Log.Logger = _log;
        GlobalExceptionHandlers.Install(this, _log);

        return paths;
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
    private void StartTheme(ServiceProvider services, ISettingsService settings)
    {
        var theme = services.GetRequiredService<ThemeService>();
        theme.Apply(settings.Current.General.Theme);

        // O WPF nao avisa quando nao acha uma fonte embutida: cai na de reserva em
        // silencio. A conferencia deixa isso no log em vez de virar um "esta estranho"
        // sem explicacao semanas depois.
        theme.VerifyFonts();

        // A troca de tema do Windows vem por SystemEvents. A janela de mensagens dos
        // atalhos (HWND_MESSAGE) nao serve: ela nunca recebe o WM_SETTINGCHANGE, que e
        // uma mensagem de difusao. O programa pode nao ter janela nenhuma visivel
        // quando o usuario troca o tema do Windows.
        theme.WatchSystem();

        // So reaplica quando o TEMA mudou. O aviso de mudanca dispara para qualquer
        // configuracao (cada tecla no campo de nome, cada passo de um controle
        // deslizante), e trocar o dicionario de tokens refaz o estilo de todas as janelas.
        ThemeMode applied = settings.Current.General.Theme;

        settings.Changed += (_, current) => Dispatcher.BeginInvoke(() =>
        {
            if (current.General.Theme == applied)
            {
                return;
            }

            applied = current.General.Theme;
            theme.Apply(applied);
        });
    }

    /// <summary>
    /// Registra os atalhos globais e liga o vigia que os reavê quando outro programa
    /// toma a tecla.
    /// </summary>
    private void StartHotkeys(ServiceProvider services, ISettingsService settings)
    {
        var hotkeys = services.GetRequiredService<HotkeyManager>();
        hotkeys.Triggered += (_, action) => Dispatcher.BeginInvoke(() => OnHotkey(action));
        hotkeys.Apply(settings.Current.Hotkeys);

        var guardian = services.GetRequiredService<HotkeyGuardian>();
        guardian.Start();

        // Atalho trocado no arquivo de configuracoes vale na hora, sem reiniciar. Mas so
        // quando os atalhos (ou o que decide se o vigia age) mudaram: o aviso dispara para
        // qualquer configuracao, e soltar e registrar de novo os dez atalhos globais a cada
        // tecla digitada em outro campo abre uma janela em que o PrtSc nao responde, e
        // reinicia o vigia a toa.
        HotkeySettings appliedHotkeys = settings.Current.Hotkeys;
        AdvancedSettings appliedGuard = settings.Current.Advanced;

        settings.Changed += (_, current) => Dispatcher.BeginInvoke(() =>
        {
            bool hotkeysChanged = current.Hotkeys != appliedHotkeys;
            bool guardChanged = current.Advanced.GuardHotkey != appliedGuard.GuardHotkey
                                || current.Advanced.OnCompetitorDetected != appliedGuard.OnCompetitorDetected;

            if (!hotkeysChanged && !guardChanged)
            {
                return;
            }

            appliedGuard = current.Advanced;

            if (hotkeysChanged)
            {
                appliedHotkeys = current.Hotkeys;
                hotkeys.Apply(appliedHotkeys);
            }

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

    /// <summary>
    /// Liga a verificação de atualização.
    /// <para>
    /// O serviço decide sozinho quando perguntar; o que o aplicativo fornece é a única
    /// coisa que só ele sabe: se dá para interromper agora.
    /// </para>
    /// </summary>
    private void StartUpdates(ServiceProvider services)
    {
        var updates = services.GetRequiredService<UpdateService>();

        // Qualquer janela visível significa trabalho em curso — o seletor sobre a
        // tela, o editor de anotação com desenho não salvo, um pin fixado, o painel
        // aberto. Reiniciar por cima de qualquer um deles destrói algo que a pessoa
        // não mandou destruir.
        updates.PodeInterromper = () => Dispatcher.Invoke(
            () => !Windows.OfType<Window>().Any(janela => janela.IsVisible));

        updates.Changed += (_, resultado) => Dispatcher.BeginInvoke(
            () => _services?.GetService<TrayIconHost>()?.ShowUpdateStatus(resultado));

        // Quando o proprio servico inicia o instalador (modo automatico, ou o que ficou
        // pendente da sessao anterior), alguem precisa encerrar o programa: o instalador
        // silencioso desiste se o mutex de instancia unica ainda estiver preso.
        updates.EncerrarParaInstalar = () => Dispatcher.BeginInvoke(() =>
        {
            _log.Information("Encerrando para a atualização ser instalada");
            Shutdown(0);
        });

        updates.Start();
    }

    /// <summary>
    /// Dispara a instalação pendente no encerramento, se for o caso.
    /// </summary>
    private void InstallPendingUpdate()
    {
        try
        {
            _services?.GetService<UpdateService>()?.InstalarAoSairSeHouver();
        }
        catch (Exception exception)
        {
            // Encerrar nunca pode falhar por causa de atualização.
            _log.Error(exception, "Falha ao iniciar a instalação da atualização");
        }
    }

    /// <summary>
    /// O Windows está encerrando a sessão (desligar, reiniciar, sair da conta).
    /// <para>
    /// Nesse caso o processo pode ser morto sem passar por <see cref="OnExit"/>, então o
    /// que só se grava lá (a configuração que ainda esperava o agendamento) precisa sair
    /// agora. O instalador pendente <b>não</b> é iniciado aqui: um instalador nascido
    /// durante o encerramento da sessão pode ser morto no meio e deixar a instalação pela
    /// metade. Ele continua guardado e é aplicado na próxima partida.
    /// </para>
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);

        _log.Information("A sessão do Windows está terminando ({Motivo})", e.ReasonSessionEnding);

        try
        {
            _services?.GetService<ISettingsService>()?.Flush();
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Falha ao gravar as configurações no fim da sessão");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Antes de qualquer descarte: se ha atualizacao baixada e a politica e
        // instalar ao sair, este e o unico instante em que da para faze-lo sem
        // interromper ninguem -- o usuario ja decidiu fechar o programa.
        InstallPendingUpdate();

        // A ordem inversa da inicializacao: o container primeiro (para o icone sair da
        // bandeja), a instancia unica depois, e o log por ultimo, para registrar tudo.
        try
        {
            _services?.Dispose();
        }
        catch (Exception exception)
        {
            // Um servico que falha ao ser descartado nao pode impedir o resto: o mutex de
            // instancia unica precisa sair sempre, senao o proximo inicio acha que ja ha um.
            _log.Error(exception, "Falha ao descartar os serviços ao encerrar");
        }

        _instanceGuard?.Dispose();

        _log.Information("Print Dev encerrado com código {Codigo}", e.ApplicationExitCode);
        Log.CloseAndFlush();

        base.OnExit(e);
    }
}
