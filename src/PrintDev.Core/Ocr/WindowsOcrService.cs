using PrintDev.Core.Capture;
using PrintDev.Core.Storage;
using Serilog;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PrintDev.Core.Ocr;

/// <summary>Resultado do reconhecimento de texto.</summary>
/// <param name="Text">O texto encontrado, já com as quebras de linha.</param>
/// <param name="Language">Idioma usado pelo motor.</param>
/// <param name="Error">Motivo de não ter dado certo, em português.</param>
public sealed record OcrResult(string Text, string? Language, string? Error)
{
    /// <summary>Verdadeiro quando há texto para copiar.</summary>
    public bool HasText => Error is null && !string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Reconhece texto usando o motor que já vem no Windows.
/// <para>
/// Sem dependência externa e sem rede: o reconhecimento acontece na máquina. Para uma
/// ferramenta que captura tela — onde passa senha, código e dado de cliente — mandar a
/// imagem para um serviço na nuvem seria a decisão errada, por mais conveniente que fosse.
/// </para>
/// <para>
/// O caso de uso é específico e comum: copiar a mensagem de erro que está numa imagem —
/// print de log, captura de terminal de outra máquina, foto de tela mandada por alguém —
/// para colar num prompt ou numa busca.
/// </para>
/// </summary>
public sealed class WindowsOcrService
{
    private readonly ILogger _log;

    public WindowsOcrService(ILogger log) => _log = log.ForContext<WindowsOcrService>();

    /// <summary>
    /// Reconhece o texto de uma captura.
    /// </summary>
    public async Task<OcrResult> RecognizeAsync(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        OcrEngine? engine = CreateEngine();
        if (engine is null)
        {
            const string message =
                "O reconhecimento de texto precisa de um pacote de idioma. Abra Configurações do " +
                "Windows, Hora e idioma, Idioma e região, e adicione o recurso de reconhecimento " +
                "óptico ao Português (Brasil).";

            _log.Warning("Nenhum motor de reconhecimento disponível.");
            return new OcrResult(string.Empty, null, message);
        }

        try
        {
            using SoftwareBitmap bitmap = await ToSoftwareBitmapAsync(image).ConfigureAwait(false);
            return await RunAsync(engine, bitmap).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Falha ao reconhecer o texto");
            return new OcrResult(string.Empty, null, "Não consegui reconhecer o texto desta captura.");
        }
    }

    private async Task<OcrResult> RunAsync(OcrEngine engine, SoftwareBitmap bitmap)
    {
        Windows.Media.Ocr.OcrResult recognized = await engine.RecognizeAsync(bitmap);

        // Uma linha por linha reconhecida: o motor devolve palavras soltas, e juntar
        // tudo num paragrafo so destruiria a estrutura de um log ou de um trecho de
        // codigo, que e justamente o que se costuma capturar.
        string text = string.Join(
            Environment.NewLine,
            recognized.Lines.Select(line => line.Text));

        _log.Information(
            "Texto reconhecido: {Linhas} linha(s) em {Idioma}",
            recognized.Lines.Count,
            engine.RecognizerLanguage.DisplayName);

        return new OcrResult(text, engine.RecognizerLanguage.DisplayName, null);
    }

    /// <summary>
    /// Escolhe o motor: português primeiro, depois os idiomas do usuário, e o inglês
    /// como último recurso.
    /// </summary>
    private OcrEngine? CreateEngine()
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language("pt-BR"))
                   ?? OcrEngine.TryCreateFromUserProfileLanguages()
                   ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Não consegui criar o motor de reconhecimento.");
            return null;
        }
    }

    /// <summary>
    /// Converte a captura para o formato que o motor aceita.
    /// <para>
    /// O caminho passa por PNG em memória de propósito. O atalho aparente — copiar os
    /// bytes direto para o buffer do <c>SoftwareBitmap</c> — dependia da extensão
    /// <c>AsBuffer</c>, que <b>não existe mais</b> no .NET moderno. Codificar e decodificar
    /// custa alguns milissegundos e é totalmente suportado.
    /// </para>
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(CapturedImage image)
    {
        byte[] png = ImageWriter.EncodePng(image);

        var stream = new InMemoryRandomAccessStream();

        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync();
    }
}
