using System.Runtime;
using System.Windows.Media;
using System.Windows.Threading;
using PrintDev.Core.Interop;

namespace PrintDev.Core.Runtime;

/// <summary>
/// Devolve ao sistema a memória que só serviu para o programa subir.
/// <para>
/// Faz sentido aqui por uma razão específica deste programa: ele passa horas parado na
/// bandeja entre um uso e outro. Ler o XAML, compilar os métodos na primeira execução e
/// carregar as configurações tocam dezenas de megabytes que nunca mais serão lidos — e
/// que, sem isto, ficam ocupando memória física até o Windows precisar dela.
/// </para>
/// <para>
/// Não é truque de aparência: as páginas saem do conjunto de trabalho de verdade. Elas
/// voltam sozinhas se forem tocadas de novo, ao custo de algumas falhas de página na
/// primeira captura depois de um longo ócio — o que é irrelevante ao lado do trabalho
/// que uma captura faz de qualquer jeito.
/// </para>
/// </summary>
public static class WorkingSet
{
    /// <summary>
    /// Espera depois do último pedido antes de liberar. Uma captura dispara vários
    /// pedidos em sequência (o seletor fecha, o aviso aparece e some); a espera junta
    /// todos numa liberação só, quando o trabalho realmente acabou.
    /// </summary>
    private static readonly TimeSpan ReleaseDelay = TimeSpan.FromSeconds(1.5);

    private static readonly Timer ReleaseTimer = new(_ => ReleaseNow(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>
    /// Pede a liberação da memória de um trabalho que acabou de terminar: captura
    /// entregue, seletor fechado, editor ou pin fechados.
    /// <para>
    /// O coletor do .NET só recolhe os blocos grandes — e toda captura de tela é um
    /// bloco grande — numa coleta completa, e uma coleta só acontece quando o programa
    /// aloca. Um capturador parado na bandeja quase não aloca: o lixo de uma captura
    /// ficava comprometido por horas, somando com o da próxima. Os bitmaps do WPF são
    /// piores ainda, porque a memória nativa deles só volta quando o finalizador roda.
    /// </para>
    /// <para>
    /// Chamar quantas vezes quiser, de qualquer thread: os pedidos próximos viram uma
    /// liberação só, numa thread de fundo, sem nunca travar a interface.
    /// </para>
    /// </summary>
    public static void ReleaseSoon()
    {
        FlushRenderReleases();
        ReleaseTimer.Change(ReleaseDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Pede um quadro ao WPF para que ele entregue as liberações pendentes ao motor de
    /// renderização.
    /// <para>
    /// Fechar uma janela não devolve as superfícies dela na hora: o WPF enfileira a
    /// liberação e só a envia no próximo quadro desenhado. Um capturador na bandeja pode
    /// passar horas sem desenhar nada, e as duas janelas de tela cheia do seletor
    /// deixavam assim uns 65 MB de superfícies nativas presas depois de fechadas —
    /// medido: eles só voltavam quando o aviso da captura seguinte aparecia.
    /// </para>
    /// <para>
    /// Assinar o evento de quadro faz o WPF desenhar um; a assinatura sai no primeiro
    /// disparo, então o custo é exatamente um quadro. Fora da thread de interface não há
    /// o que pedir, e a chamada não faz nada.
    /// </para>
    /// </summary>
    private static void FlushRenderReleases()
    {
        if (Dispatcher.FromThread(Thread.CurrentThread) is null)
        {
            return;
        }

        EventHandler? once = null;
        once = (_, _) => CompositionTarget.Rendering -= once;
        CompositionTarget.Rendering += once;
    }

    /// <summary>
    /// Coleta completa compactando, finalizadores, outra coleta, e só então o corte do
    /// conjunto de trabalho.
    /// <para>
    /// A ordem importa: cortar antes de coletar só mandaria o lixo para o arquivo de
    /// paginação, e ele continuaria contando como memória comprometida do programa —
    /// que era exatamente o que o Gerenciador de Tarefas mostrava crescendo.
    /// </para>
    /// </summary>
    private static void ReleaseNow()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            // A memoria nativa dos bitmaps so e devolvida pelo finalizador de cada um.
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

            Trim();
        }
        catch (Exception)
        {
            // Otimizacao, nao requisito: se algo recusar, o programa segue igual.
        }
    }

    /// <summary>
    /// Corta o conjunto de trabalho para o mínimo.
    /// <para>
    /// Só depois de um trabalho terminar — de preferência por <see cref="ReleaseSoon"/>,
    /// que coleta antes. Em laço ou por temporizador fixo isto deixa de ser economia e
    /// vira troca de páginas contínua com o disco.
    /// </para>
    /// </summary>
    public static bool Trim()
    {
        try
        {
            return NativeMethods.SetProcessWorkingSetSize(
                NativeMethods.GetCurrentProcess(),
                minimum: new IntPtr(-1),
                maximum: new IntPtr(-1));
        }
        catch (Exception)
        {
            // Otimização, não requisito: se o sistema recusar, o programa segue igual.
            return false;
        }
    }
}
