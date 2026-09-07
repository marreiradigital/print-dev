using System.Diagnostics;
using System.IO;

namespace PrintDev.Core.Runtime;

/// <summary>
/// Abre pastas e arquivos no shell do Windows.
/// </summary>
public static class ShellOpen
{
    /// <summary>
    /// Abre uma pasta no Explorador, criando-a se ainda não existir.
    /// </summary>
    /// <returns><see langword="true"/> se o shell aceitou abrir.</returns>
    public static bool Folder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        Directory.CreateDirectory(path);
        return Start(path);
    }

    /// <summary>
    /// Abre um arquivo no aplicativo associado. Não faz nada se o arquivo não existe:
    /// o shell mostraria uma caixa de erro feia sobre a qual não temos controle.
    /// </summary>
    public static bool File(string path)
        => !string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path) && Start(path);

    private static bool Start(string target)
    {
        try
        {
            // UseShellExecute e obrigatorio: sem ele, o .NET tenta executar o caminho
            // como processo em vez de pedir ao shell para abri-lo.
            using Process? process = Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });

            return true;
        }
        catch (Exception)
        {
            // Abrir pasta e conveniencia: nunca pode derrubar o app. Quem chama decide
            // se registra no log.
            return false;
        }
    }
}
