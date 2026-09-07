using System.Globalization;
using System.IO;
using PrintDev.Core.Infrastructure;

namespace PrintDev.Core.Configuration;

/// <summary>
/// Decide em que pasta uma captura vai ser gravada.
/// <para>
/// Fonte única de propósito: a bandeja, o painel de configurações e o gravador da
/// captura precisam concordar sobre onde os arquivos estão. Quando essa conta é
/// refeita em cada lugar, o menu abre uma pasta e o arquivo está em outra.
/// </para>
/// </summary>
public static class CaptureFolderResolver
{
    /// <summary>
    /// Pasta raiz das capturas: a configurada, ou a padrão em Imagens quando o usuário
    /// nunca escolheu uma.
    /// </summary>
    public static string ResolveRoot(AppSettings settings, IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);

        string configured = settings.Save.Folder;
        return string.IsNullOrWhiteSpace(configured) ? paths.DefaultCapturesDirectory : configured.Trim();
    }

    /// <summary>
    /// Pasta final, já com a subpasta por data quando ela estiver configurada.
    /// </summary>
    public static string Resolve(AppSettings settings, IAppPaths paths, DateTime when)
        => Path.Combine(ResolveRoot(settings, paths), DateSegment(settings.Save.DateSubfolder, when));

    /// <summary>
    /// Trecho de caminho da subpasta por data. Vazio quando não há subpasta.
    /// </summary>
    public static string DateSegment(DateSubfolder subfolder, DateTime when) => subfolder switch
    {
        // InvariantCulture e obrigatorio: com a cultura arabe ou tailandesa, o ano sairia
        // em outro calendario e as pastas do usuario ficariam irreconheciveis.
        DateSubfolder.Ano => when.ToString("yyyy", CultureInfo.InvariantCulture),
        DateSubfolder.AnoMes => Path.Combine(
            when.ToString("yyyy", CultureInfo.InvariantCulture),
            when.ToString("MM", CultureInfo.InvariantCulture)),
        DateSubfolder.AnoMesDia => Path.Combine(
            when.ToString("yyyy", CultureInfo.InvariantCulture),
            when.ToString("MM", CultureInfo.InvariantCulture),
            when.ToString("dd", CultureInfo.InvariantCulture)),
        _ => string.Empty,
    };
}
