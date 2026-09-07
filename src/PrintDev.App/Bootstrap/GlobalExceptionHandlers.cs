using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace PrintDev.Bootstrap;

/// <summary>
/// Captura as exceções que ninguém tratou, nas três origens possíveis.
/// <para>
/// Num app de bandeja isso importa mais do que o normal: sem esses ganchos, uma falha
/// derruba o processo em silêncio e o usuário só percebe quando aperta o atalho e nada
/// acontece — sem diálogo, sem log, sem pista.
/// </para>
/// </summary>
public static class GlobalExceptionHandlers
{
    /// <summary>Liga os três ganchos.</summary>
    public static void Install(Application application, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(log);

        ILogger scoped = log.ForContext(typeof(GlobalExceptionHandlers));

        // 1. Thread de interface. Da para continuar vivo na maioria dos casos.
        application.DispatcherUnhandledException += (_, e) =>
        {
            scoped.Error(e.Exception, "Exceção não tratada no dispatcher");
            e.Handled = true;
        };

        // 2. Qualquer outro thread. Aqui NAO da para impedir o encerramento: o unico
        //    ganho e registrar o motivo antes de o processo morrer.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                scoped.Fatal(exception, "Exceção não tratada; encerrando: {Encerrando}", e.IsTerminating);
            }
            else
            {
                scoped.Fatal("Falha não tratada de tipo inesperado; encerrando: {Encerrando}", e.IsTerminating);
            }

            Log.CloseAndFlush();
        };

        // 3. Task cuja excecao ninguem observou. Marcada como tratada para nao escalar.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            scoped.Error(e.Exception, "Exceção de tarefa não observada");
            e.SetObserved();
        };
    }
}
