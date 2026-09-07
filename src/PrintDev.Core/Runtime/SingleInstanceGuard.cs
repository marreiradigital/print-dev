using PrintDev.Core.Interop;

namespace PrintDev.Core.Runtime;

/// <summary>
/// Garante uma instância só do Print Dev por sessão do Windows e dá à segunda
/// instância um jeito de acordar a primeira.
/// <para>
/// Sem isso, abrir o programa duas vezes produz duas bandejas e duas tentativas de
/// registrar o mesmo atalho global — a segunda falha e o usuário fica com um ícone
/// que não responde.
/// </para>
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    // Sem prefixo de namespace, o objeto nomeado nasce no namespace da SESSAO.
    // E o que queremos: dois usuarios diferentes na mesma maquina (ou duas sessoes
    // de area de trabalho remota) podem ter cada um o seu Print Dev.
    private const string DefaultMutexName = "PrintDev.InstanciaUnica";
    private const string DefaultSignalName = "PrintDev.Ativar";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationSignal;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activationSignal, bool isPrimary)
    {
        _mutex = mutex;
        _activationSignal = activationSignal;
        IsPrimary = isPrimary;
    }

    /// <summary>
    /// <see langword="true"/> quando este processo é o dono da instância.
    /// <see langword="false"/> quando já existe outro Print Dev rodando.
    /// </summary>
    public bool IsPrimary { get; }

    /// <summary>Tenta assumir a instância única da sessão.</summary>
    public static SingleInstanceGuard Acquire(
        string mutexName = DefaultMutexName,
        string signalName = DefaultSignalName)
    {
        // initiallyOwned: false de proposito. Com true, um encerramento anormal deixa
        // o mutex "abandonado" e a proxima instancia recebe AbandonedMutexException.
        // O que interessa aqui e apenas quem criou o nome primeiro.
        var mutex = new Mutex(initiallyOwned: false, mutexName, out bool createdNew);
        var signal = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: signalName,
            createdNew: out _);

        return new SingleInstanceGuard(mutex, signal, createdNew);
    }

    /// <summary>
    /// Registra o que a instância primária faz quando outra tenta abrir.
    /// O retorno de chamada vem de um thread do pool: quem consome precisa
    /// marshalar para o dispatcher antes de tocar em interface.
    /// </summary>
    public void WhenActivationRequested(Action onActivationRequested)
    {
        ArgumentNullException.ThrowIfNull(onActivationRequested);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsPrimary)
        {
            throw new InvalidOperationException(
                "Só a instância primária pode escutar pedidos de ativação.");
        }

        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activationSignal,
            (_, _) => onActivationRequested(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Chamado pela instância secundária logo antes de encerrar, para que a primária
    /// se manifeste (abrir as configurações, piscar a bandeja) em vez de o duplo clique
    /// do usuário simplesmente não fazer nada.
    /// </summary>
    public void SignalPrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Cede o direito de primeiro plano ANTES de avisar a primaria.
        //
        // O Windows so deixa trazer janela para a frente quem acabou de receber entrada
        // do usuario - e quem recebeu o duplo clique no atalho foi ESTE processo, que
        // vai morrer em seguida. Sem esta linha a primaria abre o painel atras das
        // outras janelas, piscando na barra de tarefas, e o duplo clique parece nao ter
        // funcionado.
        NativeMethods.AllowSetForegroundWindow(NativeMethods.ASFW_ANY);

        _activationSignal.Set();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registration?.Unregister(waitObject: null);
        _registration = null;
        _activationSignal.Dispose();
        _mutex.Dispose();
    }
}
