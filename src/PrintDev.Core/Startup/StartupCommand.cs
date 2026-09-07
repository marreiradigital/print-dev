namespace PrintDev.Core.Startup;

/// <summary>
/// O que o processo deve fazer ao subir. Comandos diferentes de <see cref="Run"/>
/// executam uma tarefa pontual e encerram, sem carregar a bandeja.
/// </summary>
public enum StartupCommand
{
    /// <summary>Execução normal: bandeja, atalhos globais e captura.</summary>
    Run,

    /// <summary>
    /// Cria a tarefa do Agendador de Tarefas que inicia o Print Dev elevado no logon.
    /// Exige elevação, por isso o app se relança com este argumento.
    /// </summary>
    InstallElevatedTask,

    /// <summary>Remove a tarefa criada por <see cref="InstallElevatedTask"/>.</summary>
    UninstallElevatedTask,
}
