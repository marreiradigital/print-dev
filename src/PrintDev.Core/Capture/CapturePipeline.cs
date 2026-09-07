using System.Globalization;
using System.IO;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;
using PrintDev.Core.Storage;
using Serilog;

namespace PrintDev.Core.Capture;

/// <summary>Resultado de uma captura levada até o disco.</summary>
/// <param name="Image">A imagem capturada.</param>
/// <param name="SavedPath">Onde ficou. Nulo quando a gravação falhou.</param>
/// <param name="Error">Motivo da falha de gravação, em português.</param>
public sealed record CaptureResult(CapturedImage Image, string? SavedPath, string? Error)
{
    /// <summary>Verdadeiro quando o arquivo está no disco.</summary>
    public bool Saved => SavedPath is not null;
}

/// <summary>
/// Leva uma captura do pixel até o arquivo.
/// <para>
/// A ordem é obrigatória e não é detalhe: <b>capturar, gravar, e só então publicar o
/// caminho</b>. Colocar na área de transferência um caminho de arquivo que ainda não
/// terminou de ser escrito é o defeito clássico deste tipo de programa.
/// </para>
/// </summary>
public sealed class CapturePipeline
{
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly ILogger _log;
    private int _counter;

    public CapturePipeline(ISettingsService settings, IAppPaths paths, ILogger log)
    {
        _settings = settings;
        _paths = paths;
        _log = log.ForContext<CapturePipeline>();
    }

    /// <summary>
    /// Grava a captura conforme as configurações.
    /// </summary>
    /// <param name="image">Imagem já capturada.</param>
    /// <param name="mode">Modo usado, para o marcador de mesmo nome no arquivo.</param>
    /// <param name="activeWindow">Janela em primeiro plano no momento da captura.</param>
    public CaptureResult Save(CapturedImage image, string mode, ActiveWindowInfo? activeWindow = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        AppSettings settings = _settings.Current;
        DateTime when = DateTime.Now;
        ActiveWindowInfo window = activeWindow ?? ActiveWindowInfo.Unknown;

        var context = new FileNameContext(
            When: when,
            AppName: window.ProcessName,
            WindowTitle: window.Title,
            MonitorName: null,
            Width: image.Width,
            Height: image.Height,
            Mode: mode,
            Counter: Interlocked.Increment(ref _counter));

        string extension = ImageWriter.ExtensionOf(settings.Save.Format);
        string baseName = FileNameTemplate.Render(settings.Save.NameTemplate, context);

        foreach (string folder in CandidateFolders(settings, when))
        {
            try
            {
                Directory.CreateDirectory(folder);
                string path = ResolvePath(folder, baseName, extension, settings.Save.OnNameCollision, when);

                ImageWriter.Save(image, path, settings.Save.Format, settings.Save.JpegQuality);
                _log.Information("Captura salva em {Caminho} ({Largura}x{Altura})", path, image.Width, image.Height);

                if (image.IsCompletelyBlack())
                {
                    // Conteudo com protecao contra copia e jogo em tela cheia exclusiva
                    // vem pretos, e nao ha contorno legitimo. Avisar e melhor do que
                    // entregar um retangulo preto sem explicacao.
                    _log.Warning(
                        "A captura saiu inteiramente preta. Costuma ser conteúdo protegido contra cópia.");
                }

                return new CaptureResult(image, path, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _log.Warning(exception, "Não consegui gravar em {Pasta}; tentando o próximo destino.", folder);
            }
        }

        const string message = "Não consegui gravar a captura em nenhuma pasta.";
        _log.Error(message);

        // A imagem volta mesmo assim: quem chamou ainda pode coloca-la na area de
        // transferencia. Perder o arquivo e ruim; perder a captura e inaceitavel.
        return new CaptureResult(image, null, message);
    }

    /// <summary>
    /// Pastas a tentar, em ordem. A segunda existe para o caso de a configurada ter
    /// sumido — unidade de rede fora do ar, pendrive removido, pasta apagada.
    /// </summary>
    private IEnumerable<string> CandidateFolders(AppSettings settings, DateTime when)
    {
        yield return CaptureFolderResolver.Resolve(settings, _paths, when);
        yield return _paths.FallbackCapturesDirectory;
    }

    private static string ResolvePath(
        string folder,
        string baseName,
        string extension,
        NameCollision policy,
        DateTime when)
    {
        string candidate = Path.Combine(folder, baseName + extension);

        if (policy == NameCollision.Sobrescrever || !File.Exists(candidate))
        {
            return candidate;
        }

        if (policy == NameCollision.Milissegundos)
        {
            string stamped = $"{baseName}_{when:fff}";
            candidate = Path.Combine(folder, stamped + extension);

            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Sufixo numerico e o padrao porque nunca perde captura. O teto de 999 existe
        // so para o laco nao ser infinito num caso patologico.
        for (int suffix = 2; suffix <= 999; suffix++)
        {
            string numbered = Path.Combine(
                folder,
                $"{baseName}_{suffix.ToString(CultureInfo.InvariantCulture)}{extension}");

            if (!File.Exists(numbered))
            {
                return numbered;
            }
        }

        // Ultimo recurso: carimbo completo, que na pratica nunca colide.
        return Path.Combine(folder, $"{baseName}_{when:HHmmssfff}{extension}");
    }
}
