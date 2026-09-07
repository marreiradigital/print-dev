using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Serilog;

namespace PrintDev.Core.Runtime;

/// <summary>
/// Faz o Print Dev subir junto com o Windows.
/// </summary>
public sealed class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PrintDev";

    /// <summary>Nome da tarefa no Agendador, usado nas duas pontas.</summary>
    public const string TaskName = "PrintDev-Autostart";

    private readonly ILogger _log;

    public AutoStartService(ILogger log) => _log = log.ForContext<AutoStartService>();

    /// <summary>
    /// Caminho do executável em execução.
    /// <para>
    /// A reserva usa <see cref="AppContext.BaseDirectory"/>, e não a localização do
    /// assembly: num executável de arquivo único a localização volta <b>vazia</b>, e a
    /// entrada de inicialização apontaria para lugar nenhum — justamente na versão
    /// publicada, que é a que o usuário instala.
    /// </para>
    /// </summary>
    public static string ExecutablePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "PrintDev.exe");

    /// <summary>Existe entrada no registro para subir com o Windows.</summary>
    public bool IsRunKeyEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Existe a tarefa que sobe o programa elevado.</summary>
    public bool IsElevatedTaskInstalled => RunSchtasks($"/Query /TN \"{TaskName}\"", out _);

    /// <summary>Este processo está rodando como administrador.</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Liga ou desliga a inicialização simples, pelo registro do usuário.
    /// <para>
    /// Não exige privilégio nenhum, e o usuário vê e controla a entrada em Configurações
    /// do Windows e no Gerenciador de Tarefas — o que é honesto: um programa que se
    /// instala para subir sozinho não deveria se esconder.
    /// </para>
    /// </summary>
    public void SetRunKey(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExecutablePath}\" --silencioso");
                _log.Information("Inicialização com o Windows ligada");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _log.Information("Inicialização com o Windows desligada");
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Não consegui alterar a inicialização automática.");
        }
    }

    /// <summary>
    /// Corrige a entrada do registro quando o executável mudou de lugar.
    /// <para>
    /// Sem isso, mover a pasta do programa deixa uma entrada apontando para o nada — e o
    /// usuário só descobre no próximo logon, quando o atalho simplesmente não funciona.
    /// </para>
    /// </summary>
    public void RepairIfMoved()
    {
        if (!IsRunKeyEnabled)
        {
            return;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string value)
            {
                return;
            }

            string expected = $"\"{ExecutablePath}\" --silencioso";
            if (!string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, expected);
                _log.Information("Entrada de inicialização corrigida para o caminho atual.");
            }
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Não consegui conferir a entrada de inicialização.");
        }
    }

    /// <summary>
    /// Pede a criação da tarefa que sobe o programa <b>elevado</b> no logon.
    /// <para>
    /// É a única forma de o atalho global funcionar com uma janela de administrador em
    /// primeiro plano: o Windows não entrega a tecla a um processo de integridade mais
    /// baixa que a janela em foco.
    /// </para>
    /// <para>
    /// Criar a tarefa exige privilégio, então o programa se relança elevado uma única
    /// vez. Depois disso a tarefa sobe sozinha, <b>sem prompt de UAC no logon</b>.
    /// </para>
    /// </summary>
    public bool RequestElevatedTask(bool install)
    {
        if (IsElevated)
        {
            return install ? InstallElevatedTask() : RemoveElevatedTask();
        }

        try
        {
            var start = new ProcessStartInfo(ExecutablePath)
            {
                Arguments = install ? "--install-elevated-task" : "--uninstall-elevated-task",
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(start);
            return true;
        }
        catch (Exception exception)
        {
            // O usuario pode simplesmente recusar o pedido de elevacao, e isso e uma
            // resposta valida - nao um erro do programa.
            _log.Information(exception, "A elevação foi recusada ou falhou.");
            return false;
        }
    }

    /// <summary>Cria a tarefa. Precisa estar rodando elevado.</summary>
    public bool InstallElevatedTask()
    {
        // /RL HIGHEST e o que faz a tarefa subir elevada; /SC ONLOGON e o gatilho; /F
        // sobrescreve uma tarefa anterior em vez de falhar.
        string arguments =
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{ExecutablePath}\\\" --silencioso\" " +
            $"/SC ONLOGON /RL HIGHEST /F";

        if (RunSchtasks(arguments, out string output))
        {
            _log.Information("Tarefa de inicialização elevada criada.");

            // As duas formas juntas fariam o programa abrir duas vezes; a instancia
            // unica barraria a segunda, mas o processo extra ainda seria criado.
            SetRunKey(false);
            return true;
        }

        _log.Error("Não consegui criar a tarefa de inicialização: {Saida}", output);
        return false;
    }

    /// <summary>Remove a tarefa.</summary>
    public bool RemoveElevatedTask()
    {
        if (RunSchtasks($"/Delete /TN \"{TaskName}\" /F", out string output))
        {
            _log.Information("Tarefa de inicialização elevada removida.");
            return true;
        }

        _log.Warning("Não consegui remover a tarefa de inicialização: {Saida}", output);
        return false;
    }

    private static bool RunSchtasks(string arguments, out string output)
    {
        output = string.Empty;

        try
        {
            var start = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);

            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception exception)
        {
            output = exception.Message;
            return false;
        }
    }
}
