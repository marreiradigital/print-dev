using System.IO;
using System.Text.RegularExpressions;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Storage;
using Serilog;

namespace PrintDev.Core.Maintenance;

/// <summary>Um arquivo que a limpeza removeria, e o motivo.</summary>
/// <param name="Path">Caminho completo.</param>
/// <param name="SizeBytes">Tamanho.</param>
/// <param name="Modified">Data da última modificação.</param>
/// <param name="Reason">Por que ele entrou na lista.</param>
public sealed record CleanupCandidate(string Path, long SizeBytes, DateTime Modified, string Reason);

/// <summary>
/// Remove capturas antigas.
/// <para>
/// É a única parte do programa que apaga arquivo do usuário, e por isso é a mais
/// desconfiada: nasce desligada, só olha dentro da pasta configurada, só reconhece
/// arquivo cujo nome casa com o modelo do Print Dev, nunca atravessa link simbólico e
/// manda para a Lixeira.
/// </para>
/// <para>
/// A varredura tem um modo que só <b>lista</b> o que seria removido. Ligar uma limpeza
/// automática sem poder ver antes o que ela vai fazer é pedir para o usuário confiar às
/// cegas numa operação irreversível.
/// </para>
/// </summary>
public sealed class CleanupService
{
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger _log;

    public CleanupService(ISettingsService settings, IAppPaths paths, ILogger log)
    {
        _settings = settings;
        _paths = paths;
        _log = log.ForContext<CleanupService>();
    }

    /// <summary>
    /// Lista o que seria removido, sem tocar em nada.
    /// </summary>
    public IReadOnlyList<CleanupCandidate> Preview()
    {
        AppSettings settings = _settings.Current;
        CleanupSettings cleanup = settings.Cleanup;
        string root = CaptureFolderResolver.ResolveRoot(settings, _paths);

        if (!Directory.Exists(root))
        {
            return [];
        }

        Regex pattern = FileNameTemplate.BuildPattern(settings.Save.NameTemplate);
        DateTime cutoff = DateTime.Now.AddDays(-Math.Max(1, cleanup.KeepDays));
        long maxBytes = Math.Max(1, cleanup.MaxSizeMb) * 1024L * 1024L;

        List<FileInfo> ours = Collect(root, pattern);

        // Da mais nova para a mais antiga: o teto de tamanho corta a partir do fim,
        // preservando sempre as capturas recentes.
        ours.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));

        var candidates = new List<CleanupCandidate>();
        long running = 0;

        foreach (FileInfo file in ours)
        {
            running += file.Length;

            if (file.LastWriteTime < cutoff)
            {
                candidates.Add(new CleanupCandidate(
                    file.FullName, file.Length, file.LastWriteTime,
                    $"mais de {cleanup.KeepDays} dias"));
            }
            else if (running > maxBytes)
            {
                candidates.Add(new CleanupCandidate(
                    file.FullName, file.Length, file.LastWriteTime,
                    $"acima do limite de {cleanup.MaxSizeMb} MB"));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Executa a limpeza, se ela estiver ligada.
    /// </summary>
    /// <returns>Quantos arquivos saíram.</returns>
    public int Run()
    {
        if (!_settings.Current.Cleanup.Enabled)
        {
            return 0;
        }

        IReadOnlyList<CleanupCandidate> candidates = Preview();
        if (candidates.Count == 0)
        {
            return 0;
        }

        bool toRecycleBin = _settings.Current.Cleanup.MoveToRecycleBin;
        int removed = 0;

        foreach (CleanupCandidate candidate in candidates)
        {
            try
            {
                if (toRecycleBin ? RecycleBin.Send(candidate.Path) : TryDelete(candidate.Path))
                {
                    removed++;
                }
            }
            catch (Exception exception)
            {
                _log.Warning(exception, "Não consegui remover {Arquivo}", candidate.Path);
            }
        }

        if (removed > 0)
        {
            _log.Information(
                "Limpeza automática: {Quantidade} captura(s) removida(s) ({Destino})",
                removed,
                toRecycleBin ? "Lixeira" : "apagadas");
        }

        return removed;
    }

    /// <summary>
    /// Junta os arquivos que são <b>nossos</b>, respeitando a trava de segurança.
    /// </summary>
    private List<FileInfo> Collect(string root, Regex pattern)
    {
        bool onlyOurs = _settings.Current.Cleanup.OnlyPrintDevFiles;
        var found = new List<FileInfo>();

        try
        {
            var directory = new DirectoryInfo(root);

            foreach (FileInfo file in directory.EnumerateFiles("*", new EnumerationOptions
            {
                RecurseSubdirectories = true,

                // Link simbolico e ponto de juncao ficam de fora: seguir um deles faria a
                // limpeza sair da pasta configurada sem ninguem perceber.
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden,
                IgnoreInaccessible = true,
            }))
            {
                if (onlyOurs && !pattern.IsMatch(file.Name))
                {
                    continue;
                }

                if (!onlyOurs && !IsImage(file.Extension))
                {
                    continue;
                }

                found.Add(file);
            }
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Não consegui varrer {Pasta}", root);
        }

        return found;
    }

    private static bool IsImage(string extension)
        => extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static bool TryDelete(string path)
    {
        File.Delete(path);
        return true;
    }
}
