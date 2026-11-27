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

    private static HotkeySettings OnlyTestKey() => new()
    {
        Capture = TestCombo,
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
