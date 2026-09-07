using System.Windows;
using PrintDev.Core.Startup;

namespace PrintDev;

/// <summary>
/// Ponto de entrada do Print Dev.
/// </summary>
public partial class App : Application
{
    /// <summary>Argumentos de linha de comando já interpretados.</summary>
    public StartupOptions Options { get; private set; } = StartupOptions.Default;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Options = StartupOptions.Parse(e.Args);

        // TODO(fase 1): montar o container, o log em arquivo, a instância única
        // e o ícone da bandeja. Enquanto isso não existe, o processo encerra em vez
        // de ficar vivo e invisível — um app de bandeja sem bandeja só dá trabalho
        // para o usuário matar no Gerenciador de Tarefas.
        Shutdown(0);
    }
}
