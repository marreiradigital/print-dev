using System.IO;
using System.Text;

namespace PrintDev.Core.Configuration;

/// <summary>
/// Grava arquivo de texto sem deixar o original meio escrito.
/// <para>
/// O caminho ingênuo — abrir o arquivo final e escrever por cima — perde tudo se a
/// energia cair, o processo for morto ou o disco encher no meio. Como o alvo aqui é o
/// arquivo de configuração do usuário, isso significaria voltar aos padrões sem aviso.
/// </para>
/// </summary>
public static class AtomicFileWriter
{
    /// <summary>
    /// Escreve num arquivo temporário ao lado do destino, força a descarga para o
    /// disco e só então troca o destino pelo temporário, guardando o anterior como
    /// cópia de segurança.
    /// </summary>
    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        encoding ??= new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        string backup = path + ".bak";

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, encoding))
        {
            writer.Write(content);
            writer.Flush();

            // flushToDisk: true e o que diferencia isto de uma gravacao comum. Sem ele,
            // o conteudo pode estar so no cache do sistema quando a troca acontece.
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
        {
            // File.Replace e atomico no NTFS e ainda deixa a versao anterior no .bak.
            File.Replace(temporary, path, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temporary, path);
        }
    }
}
