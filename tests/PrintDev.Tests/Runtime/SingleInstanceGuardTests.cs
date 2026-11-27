using System.Security.AccessControl;
using System.Security.Principal;
using PrintDev.Core.Runtime;

namespace PrintDev.Tests.Runtime;

/// <summary>
/// Os testes usam nomes próprios: nunca tocam no mutex do Print Dev que possa estar
/// rodando na máquina de quem executa a suíte.
/// </summary>
public class SingleInstanceGuardTests
{
    private static (string Mutex, string Signal) NewNames()
    {
        string id = Guid.NewGuid().ToString("N");
        return ($"PrintDev.Teste.Mutex.{id}", $"PrintDev.Teste.Sinal.{id}");
    }

    [Fact]
    public void A_primeira_instancia_e_primaria_e_a_segunda_nao()
    {
        (string mutex, string signal) = NewNames();

        using SingleInstanceGuard first = SingleInstanceGuard.Acquire(mutex, signal);
        using SingleInstanceGuard second = SingleInstanceGuard.Acquire(mutex, signal);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void A_segunda_instancia_acorda_a_primaria()
    {
        (string mutex, string signal) = NewNames();

        using SingleInstanceGuard first = SingleInstanceGuard.Acquire(mutex, signal);
        using SingleInstanceGuard second = SingleInstanceGuard.Acquire(mutex, signal);
        using var acordou = new ManualResetEventSlim(false);

        first.WhenActivationRequested(acordou.Set);
        second.SignalPrimary();

        Assert.True(acordou.Wait(TimeSpan.FromSeconds(10)), "A primária não foi acordada.");
    }

    [Fact]
    public void Os_objetos_nascem_com_acesso_explicito_para_o_usuario_e_para_administradores()
    {
        // Sem descritor explicito, um Print Dev elevado criava o mutex so para
        // administradores e o duplo clique sem elevacao tomava UnauthorizedAccessException.
        (string mutex, string signal) = NewNames();

        using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(mutex, signal);

        using Mutex aberto = MutexAcl.OpenExisting(mutex, MutexRights.ReadPermissions);
        AuthorizationRuleCollection rules = aberto.GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));

        SecurityIdentifier user = WindowsIdentity.GetCurrent().User!;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        Assert.Contains(rules.OfType<MutexAccessRule>(), r =>
            r.IdentityReference == user
            && r.AccessControlType == AccessControlType.Allow
            && r.MutexRights.HasFlag(MutexRights.FullControl));

        Assert.Contains(rules.OfType<MutexAccessRule>(), r =>
            r.IdentityReference == admins && r.AccessControlType == AccessControlType.Allow);
    }

    [Fact]
    public void Mutex_que_nao_da_para_abrir_significa_que_ja_ha_outra_instancia_e_nao_lanca()
    {
        // Reproduz o que uma instancia elevada de uma versao anterior deixava: um objeto
        // nomeado que o usuario comum nao consegue abrir.
        (string mutex, string signal) = NewNames();

        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            MutexRights.FullControl,
            AccessControlType.Allow));

        using Mutex alheio = MutexAcl.Create(initiallyOwned: false, mutex, out bool novo, security);
        Assert.True(novo);

        using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(mutex, signal);

        Assert.False(guard.IsPrimary);

        // E sair em silencio e o que se espera da secundaria.
        guard.SignalPrimary();
    }
}
