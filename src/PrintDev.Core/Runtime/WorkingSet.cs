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
    /// Corta o conjunto de trabalho para o mínimo.
    /// <para>
    /// Chamar <b>uma vez</b>, quando a inicialização termina. Em laço ou por temporizador
    /// isto deixa de ser economia e vira troca de páginas contínua com o disco.
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
