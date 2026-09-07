using System.Diagnostics;

namespace PrintDev.Core.Hotkeys;

/// <summary>Um programa que provavelmente está segurando a tecla de atalho.</summary>
/// <param name="ProcessName">Nome do processo, sem extensão.</param>
/// <param name="FriendlyName">Nome que o usuário reconhece.</param>
/// <param name="ProcessIds">Processos vivos com esse nome.</param>
public sealed record Competitor(string ProcessName, string FriendlyName, IReadOnlyList<int> ProcessIds);

/// <summary>
/// Descobre qual programa está com a tecla quando o registro do atalho falha.
/// <para>
/// Sem isto, o usuário recebe "não foi possível registrar o atalho" — uma frase que não
/// diz o que fazer. Nomear o culpado transforma o erro numa ação: fechar aquele
/// programa.
/// </para>
/// </summary>
public static class CompetitorDetector
{
    /// <summary>
    /// Capturadores conhecidos que registram PrtSc. O OneDrive entra na lista porque a
    /// opção "salvar capturas de tela automaticamente" dele também toma a tecla, e
    /// quase ninguém associa uma coisa à outra.
    /// </summary>
    private static readonly (string Process, string Friendly)[] Known =
    [
        ("Lightshot", "Lightshot"),
        ("ShareX", "ShareX"),
        ("Greenshot", "Greenshot"),
        ("SnagitEditor", "Snagit"),
        ("Snagit32", "Snagit"),
        ("SnagPriv", "Snagit"),
        ("PicPick", "PicPick"),
        ("flameshot", "Flameshot"),
        ("Screenpresso", "Screenpresso"),
        ("Gyazo", "Gyazo"),
        ("Joxi", "Joxi"),
        ("ScreenSketch", "Ferramenta de Captura"),
        ("OneDrive", "OneDrive (salvar capturas automaticamente)"),
    ];

    /// <summary>
    /// Lista os capturadores conhecidos que estão rodando agora.
    /// </summary>
    public static IReadOnlyList<Competitor> FindRunning()
    {
        var found = new List<Competitor>();

        foreach ((string process, string friendly) in Known)
        {
            int[] ids;
            try
            {
                ids = Process.GetProcessesByName(process).Select(p => p.Id).ToArray();
            }
            catch (Exception)
            {
                // Enumerar processo pode falhar por permissao ou por o processo morrer
                // no meio da varredura. Diagnostico nunca pode derrubar quem chamou.
                continue;
            }

            if (ids.Length > 0)
            {
                found.Add(new Competitor(process, friendly, ids));
            }
        }

        return found;
    }

    /// <summary>
    /// Encerra os processos de um concorrente.
    /// </summary>
    /// <returns><see langword="true"/> se todos saíram.</returns>
    public static bool Close(Competitor competitor)
    {
        ArgumentNullException.ThrowIfNull(competitor);

        bool allClosed = true;

        foreach (int id in competitor.ProcessIds)
        {
            try
            {
                using Process process = Process.GetProcessById(id);
                process.Kill();

                // Sem a espera, o atalho ainda esta registrado quando tentamos assumir:
                // o Windows so libera a tecla quando o processo dono some de verdade.
                allClosed &= process.WaitForExit(3000);
            }
            catch (ArgumentException)
            {
                // Ja tinha morrido entre a deteccao e agora. Conta como sucesso.
            }
            catch (Exception)
            {
                allClosed = false;
            }
        }

        return allClosed;
    }
}
