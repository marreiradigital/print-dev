using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;

namespace PrintDev.Core.Storage;

/// <summary>
/// Grava a captura em disco e produz os bytes usados pela área de transferência.
/// <para>
/// A codificação usa os codecs de imagem do Windows, que já vêm com o WPF. Nada de
/// GDI+: além de ser tratado como legado, obrigaria a gerenciar o descarte de objetos
/// gráficos em cada caminho de erro.
/// </para>
/// </summary>
public static class ImageWriter
{
    /// <summary>Extensão de arquivo de cada formato.</summary>
    public static string ExtensionOf(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        _ => ".png",
    };

    /// <summary>
    /// Grava a imagem no caminho indicado, de forma atômica.
    /// <para>
    /// O arquivo aparece inteiro ou não aparece. Isso importa porque o caminho vai para
    /// a área de transferência logo depois, e o programa de destino pode abrir o arquivo
    /// em milissegundos — um PNG pela metade viraria uma imagem quebrada na tela de
    /// quem colou.
    /// </para>
    /// </summary>
    public static void Save(CapturedImage image, string path, ImageFormat format, int jpegQuality)
    {
        ArgumentNullException.ThrowIfNull(image);
        WriteAtomically(path, Encode(image, format, jpegQuality).Span);
    }

    /// <summary>
    /// Grava bytes já codificados no caminho indicado, de forma atômica.
    /// <para>
    /// O arquivo aparece inteiro ou não aparece. Isso importa porque o caminho vai para
    /// a área de transferência logo depois, e o programa de destino pode abrir o arquivo
    /// em milissegundos — um PNG pela metade viraria uma imagem quebrada na tela de
    /// quem colou.
    /// </para>
    /// </summary>
    public static void WriteAtomically(string path, ReadOnlySpan<byte> encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";

        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(encoded);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Codifica a imagem em memória, no formato pedido.
    /// <para>
    /// Devolve uma janela sobre o buffer do codificador, e não uma cópia dele: a captura
    /// já passa pelo disco e pela área de transferência, e cada cópia a mais de uma tela
    /// cheia é mais um bloco grande esperando o coletor.
    /// </para>
    /// </summary>
    public static ReadOnlyMemory<byte> Encode(CapturedImage image, ImageFormat format, int jpegQuality)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Captura de tela comprime bem: um quarto dos bytes crus quase sempre basta, e
        // reservar de uma vez evita a sequencia de buffers dobrando de tamanho.
        int estimate = Math.Max(64 * 1024, image.Pixels.Length / 4);

        var stream = new MemoryStream(estimate);
        CreateEncoder(image, format, jpegQuality).Save(stream);
        return stream.GetBuffer().AsMemory(0, (int)stream.Length);
    }

    /// <summary>
    /// Codifica a imagem como PNG em memória.
    /// <para>
    /// A área de transferência recebe <b>sempre</b> PNG, mesmo quando o arquivo em disco
    /// é JPEG: o Chromium — e portanto WhatsApp Web, Discord, Slack e todo aplicativo
    /// Electron — procura o formato PNG registrado antes de qualquer outro, e não lê
    /// JPEG da área de transferência.
    /// </para>
    /// </summary>
    public static ReadOnlyMemory<byte> EncodePng(CapturedImage image)
        => Encode(image, ImageFormat.Png, jpegQuality: 100);

    /// <summary>
    /// Verdadeiro quando os bytes começam com a assinatura do PNG.
    /// <para>
    /// Serve para reaproveitar o arquivo em disco como está na área de transferência,
    /// em vez de decodificar e codificar de novo a mesma imagem. A extensão não basta:
    /// o modelo de nome é do usuário, e nada impede um <c>.png</c> que não é PNG.
    /// </para>
    /// </summary>
    public static bool IsPng(ReadOnlySpan<byte> bytes)
        => bytes.Length > PngSignature.Length && bytes[..PngSignature.Length].SequenceEqual(PngSignature);

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static BitmapEncoder CreateEncoder(CapturedImage image, ImageFormat format, int jpegQuality)
    {
        BitmapSource source = image.ToBitmapSource();

        if (format == ImageFormat.Jpeg)
        {
            // JPEG nao tem canal alfa. Converter antes evita que o codificador decida
            // sozinho, e o resultado e previsivel.
            var opaque = new FormatConvertedBitmap(source, PixelFormats.Bgr24, destinationPalette: null, alphaThreshold: 0);
            opaque.Freeze();

            var jpeg = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
            jpeg.Frames.Add(BitmapFrame.Create(opaque));
            return jpeg;
        }

        var png = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
        png.Frames.Add(BitmapFrame.Create(source));
        return png;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Limpeza de melhor esforco: nao pode mascarar a excecao original, que e a
            // que realmente diz por que a gravacao falhou.
        }
    }
}
