using System.Security.AccessControl;
using System.Security.Principal;
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

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activationSignal;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstanceGuard(Mutex? mutex, EventWaitHandle? activationSignal, bool isPrimary)
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

    /// <summary>
    /// Tenta assumir a instância única da sessão. Nunca lança por causa de permissão:
    /// objeto que existe mas não dá para abrir significa, com certeza, que há outro
    /// Print Dev rodando.
    /// </summary>
    public static SingleInstanceGuard Acquire(
        string mutexName = DefaultMutexName,
        string signalName = DefaultSignalName)
    {
        // initiallyOwned: false de proposito. Com true, um encerramento anormal deixa
        // o mutex "abandonado" e a proxima instancia recebe AbandonedMutexException.
        // O que interessa aqui e apenas quem criou o nome primeiro.
        //
        // O descritor de seguranca e explicito de proposito. Sem ele o objeto herda a
        // DACL padrao do token de QUEM CRIA, e um Print Dev elevado (a tarefa do
        // Agendador sobe assim) cria o mutex so para administradores: o duplo clique
        // seguinte, sem elevacao, levava UnauthorizedAccessException antes de qualquer
        // tratador de erro existir, e o programa morria sem uma palavra.
        Mutex? mutex;
        bool isPrimary;

        try
        {
            mutex = MutexAcl.Create(initiallyOwned: false, mutexName, out bool createdNew, CreateMutexSecurity());
            isPrimary = createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            // O objeto existe, foi criado por um processo mais privilegiado (ou por uma
            // versao anterior, sem o descritor) e nao nos deixa abrir. Ha outro Print Dev.
            mutex = null;
            isPrimary = false;
        }

        EventWaitHandle? signal;

        try
        {
            signal = EventWaitHandleAcl.Create(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: signalName,
                createdNew: out _,
                eventSecurity: CreateSignalSecurity());
        }
        catch (UnauthorizedAccessException)
        {
            // Sem o sinal nao da para acordar a primaria; tenta o minimo de direitos
            // (so sinalizar). Se nem isso, o duplo clique apenas nao abre o painel.
            signal = TryOpenSignal(signalName);
        }

        return new SingleInstanceGuard(mutex, signal, isPrimary);
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

        if (_activationSignal is null)
        {
            // Nao deu para criar o sinal: o programa funciona, so nao reage ao duplo clique.
            return;
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

        try
        {
            _activationSignal?.Set();
        }
        catch (UnauthorizedAccessException)
        {
            // Sem permissao para sinalizar: nao ha o que fazer alem de sair em silencio.
        }
    }

    /// <summary>
    /// Quem pode abrir os objetos: o usuário desta sessão e os administradores.
    /// Elevado ou não, o mesmo usuário tem o mesmo SID — é isso que deixa um processo
    /// sem elevação enxergar o objeto criado por um elevado, e vice-versa.
    /// </summary>
    private static IEnumerable<SecurityIdentifier> AllowedIdentities()
    {
        // O token do processo e um handle: sem o using ele ficaria aberto ate o fim.
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier? user = identity.User;

        if (user is not null)
        {
            yield return user;
        }

        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    }

    private static MutexSecurity CreateMutexSecurity()
    {
        var security = new MutexSecurity();

        foreach (SecurityIdentifier identity in AllowedIdentities())
        {
            security.AddAccessRule(new MutexAccessRule(identity, MutexRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private static EventWaitHandleSecurity CreateSignalSecurity()
    {
        var security = new EventWaitHandleSecurity();

        foreach (SecurityIdentifier identity in AllowedIdentities())
        {
            security.AddAccessRule(
                new EventWaitHandleAccessRule(identity, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private static EventWaitHandle? TryOpenSignal(string signalName)
    {
        try
        {
            return EventWaitHandleAcl.OpenExisting(signalName, EventWaitHandleRights.Modify);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
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
        _activationSignal?.Dispose();
        _mutex?.Dispose();
    }
}
