using System.Windows.Threading;
using PrintDev.Core.Configuration;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Interop;

namespace PrintDev.Tests.Hotkeys;

/// <summary>
/// O <c>RegisterHotKey</c> só vale na thread dona da janela. Estes testes sobem uma thread de
/// interface de verdade (com laço de mensagens) e chamam o gerente e o vigia de fora dela —
/// que é exatamente o que o temporizador de pool fazia, e falhava com
/// <c>ERROR_WINDOW_OF_OTHER_THREAD</c>.
/// <para>
/// Usam uma combinação que ninguém usa (<c>Ctrl+Alt+Shift+F24</c>), registrada por alguns
/// milissegundos, e nunca as teclas reais do Print Dev.
/// </para>
/// </summary>
public sealed class HotkeyThreadingTests : IDisposable
{
    private const string TestCombo = "Ctrl+Alt+Shift+F24";
    private const int HolderId = 7001;

    private readonly Thread _thread;
    private readonly Dispatcher _ui;

    public HotkeyThreadingTests()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "PrintDev.Teste.Interface",
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)));
        _ui = dispatcher!;
    }

    /// <summary>Nenhum atalho: ninguém registra nada de verdade.</summary>
    private static HotkeySettings Blank() => new()
    {
        Capture = string.Empty,
        FullScreen = string.Empty,
        ActiveWindow = string.Empty,
        RepeatLastRegion = string.Empty,
        PasteAsPath = string.Empty,
        PasteAsImage = string.Empty,
        ColorPicker = string.Empty,
        Ocr = string.Empty,
        UndoLastCapture = string.Empty,
        SendToCloud = string.Empty,
    };

    private static HotkeySettings OnlyTestKey() => Blank() with { Capture = TestCombo };

    private T OnUi<T>(Func<T> work) => _ui.Invoke(work);

    /// <summary>Outra janela do mesmo processo segurando a tecla de teste.</summary>
    private HotkeyMessageWindow HoldTestKey()
    {
        HotkeyMessageWindow holder = OnUi(() => new HotkeyMessageWindow());

        Assert.True(HotkeyParser.TryParse(TestCombo, out HotkeyDefinition combo, out _));
        bool held = OnUi(() => NativeMethods.RegisterHotKey(
            holder.Handle, HolderId, combo.ModifiersForRegistration, combo.VirtualKey));

        Assert.True(held, $"Não consegui segurar {TestCombo} para o teste: outro programa já a usa?");
        return holder;
    }

    private void ReleaseTestKey(HotkeyMessageWindow holder)
        => OnUi(() => NativeMethods.UnregisterHotKey(holder.Handle, HolderId));

    [Fact]
    public void Atalho_tomado_e_recuperado_por_uma_chamada_vinda_de_outra_thread()
    {
        HotkeyMessageWindow holder = HoldTestKey();
        HotkeyMessageWindow window = OnUi(() => new HotkeyMessageWindow());
        HotkeyManager manager = OnUi(() => new HotkeyManager(window, Serilog.Core.Logger.None));

        try
        {
            // Apply tambem vem de fora da thread de interface.
            manager.Apply(OnlyTestKey());

            Assert.Single(manager.Failures);
            Assert.Contains("já está sendo usado", manager.Failures[0].Error);

            ReleaseTestKey(holder);

            // Chamada da thread do teste, nao da de interface: sem devolver o trabalho para
            // a thread dona da janela, o Windows recusava e nada era recuperado.
            int recovered = manager.RetryFailures();

            Assert.Equal(1, recovered);
            Assert.Empty(manager.Failures);
            Assert.True(manager.Registrations[HotkeyAction.Capture].Registered);
        }
        finally
        {
            OnUi(() =>
            {
                manager.Dispose();
                window.Dispose();
                holder.Dispose();
                return 0;
            });
        }
    }

    [Fact]
    public void O_vigia_reassume_sozinho_o_atalho_que_foi_tomado()
    {
        HotkeyMessageWindow holder = HoldTestKey();
        HotkeyMessageWindow window = OnUi(() => new HotkeyMessageWindow());
        HotkeyManager manager = OnUi(() => new HotkeyManager(window, Serilog.Core.Logger.None));
        HotkeyGuardian guardian = OnUi(() => new HotkeyGuardian(
            manager, new FixedSettings(), Serilog.Core.Logger.None, TimeSpan.FromMilliseconds(100)));

        try
        {
            manager.Apply(OnlyTestKey());
            Assert.Single(manager.Failures);

            guardian.Start();
            Thread.Sleep(250);
            ReleaseTestKey(holder);

            // Quem recupera e o disparo do temporizador do vigia, nao uma chamada direta.
            Assert.True(
                SpinWait.SpinUntil(() => manager.Failures.Count == 0, TimeSpan.FromSeconds(15)),
                "O vigia não reassumiu o atalho depois que ele foi liberado.");
        }
        finally
        {
            OnUi(() =>
            {
                guardian.Dispose();
                manager.Dispose();
                window.Dispose();
                holder.Dispose();
                return 0;
            });
        }
    }

    // ==================== O que aparece como falha ====================

    [Fact]
    public void Atalho_escrito_errado_aparece_nas_falhas_com_o_motivo_e_nao_e_reinsistido()
    {
        // O filtro era "tem tecla", e texto que nao se le vira "sem tecla": o erro dele
        // nunca chegava ao aviso do painel. E o guardiao nao deve insistir nele.
        HotkeyMessageWindow window = OnUi(() => new HotkeyMessageWindow());
        HotkeyManager manager = OnUi(() => new HotkeyManager(window, Serilog.Core.Logger.None));

        try
        {
            manager.Apply(Blank() with { Capture = "Ctrl+Banana", FullScreen = "Enter" });

            Assert.Equal(2, manager.Failures.Count);
            Assert.Contains(manager.Failures, f => f.Action == HotkeyAction.Capture && f.Error!.Contains("Banana"));
            Assert.Contains(manager.Failures, f => f.Action == HotkeyAction.FullScreen && f.Error!.Contains("Ctrl, Alt ou Win"));

            // Texto que nao melhora sozinho nao e tentado de novo a cada 30 segundos.
            Assert.Empty(manager.RetryableFailures);
            Assert.Equal(0, manager.RetryFailures());

            // E o campo em branco continua sendo so "sem atalho", nao falha.
            Assert.DoesNotContain(manager.Failures, f => f.Action == HotkeyAction.Ocr);
        }
        finally
        {
            OnUi(() =>
            {
                manager.Dispose();
                window.Dispose();
                return 0;
            });
        }
    }

    [Fact]
    public void Duas_acoes_na_mesma_combinacao_nao_sao_culpa_de_outro_programa()
    {
        // O Windows recusa a segunda com o mesmo erro de "ja registrado" que usa para
        // outro programa, e a mensagem mandava o usuario procurar o culpado fora.
        HotkeyMessageWindow window = OnUi(() => new HotkeyMessageWindow());
        HotkeyManager manager = OnUi(() => new HotkeyManager(window, Serilog.Core.Logger.None));

        try
        {
            manager.Apply(Blank() with { Capture = TestCombo, FullScreen = TestCombo });

            Assert.True(manager.Registrations[HotkeyAction.Capture].Registered);

            HotkeyRegistration repetida = Assert.Single(manager.Failures);
            Assert.Equal(HotkeyAction.FullScreen, repetida.Action);
            Assert.Contains("Abrir o seletor", repetida.Error);
            Assert.DoesNotContain("outro programa", repetida.Error);

            // So mudar a configuracao resolve: o vigia nao insiste.
            Assert.Empty(manager.RetryableFailures);
        }
        finally
        {
            OnUi(() =>
            {
                manager.Dispose();
                window.Dispose();
                return 0;
            });
        }
    }

    public void Dispose()
    {
        _ui.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
        GC.SuppressFinalize(this);
    }

    /// <summary>Configurações padrão, sem arquivo: o vigia só lê <c>Current</c>.</summary>
    private sealed class FixedSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

#pragma warning disable CS0067 // o vigia nunca assina
        public event EventHandler<AppSettings>? Changed;
#pragma warning restore CS0067

        public void Load()
        {
        }

        public void Update(Func<AppSettings, AppSettings> change)
        {
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }
}
