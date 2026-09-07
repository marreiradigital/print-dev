using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PrintDev.Core.Capture;
using PrintDev.Core.Interop;
using PrintDev.Core.Storage;
using Serilog;

namespace PrintDev.Core.Clipboard;

/// <summary>O que publicar na área de transferência.</summary>
/// <param name="Image">A captura. Nulo publica só o texto.</param>
/// <param name="Text">Texto do caminho. Nulo publica só a imagem.</param>
/// <param name="FilePath">Caminho real em disco, para o formato de arquivo.</param>
/// <param name="IncludeFileDrop">Publicar também o formato de arquivo.</param>
public sealed record ClipboardPayload(
    CapturedImage? Image,
    string? Text,
    string? FilePath = null,
    bool IncludeFileDrop = false);

/// <summary>
/// Publica a captura em vários formatos numa única transação.
/// <para>
/// <b>É o coração do produto.</b> Nenhum programa pode saber para onde o usuário vai
/// colar. O que resolve isso é publicar vários formatos de uma vez, na ordem certa: o
/// programa de destino pega o primeiro que sabe consumir. Campo que aceita imagem acha
/// a imagem antes; campo que só aceita texto acha o caminho.
/// </para>
/// </summary>
public sealed class ClipboardWriter
{
    /// <summary>
    /// Formato PNG registrado por nome. <b>O Chromium procura este primeiro</b>, antes
    /// de qualquer bitmap — e Chromium aqui significa WhatsApp Web, Discord, Slack,
    /// Figma e todo aplicativo Electron. Publicá-lo garante a imagem exata, sem passar
    /// por uma conversão para bitmap e voltar.
    /// </summary>
    private static readonly Lazy<uint> PngFormat = new(() => NativeMethods.RegisterClipboardFormat("PNG"));

    /// <summary>Diz ao Explorador que a operação é cópia, não recorte.</summary>
    private static readonly Lazy<uint> PreferredDropEffect =
        new(() => NativeMethods.RegisterClipboardFormat("Preferred DropEffect"));

    /// <summary>Caminho único, entendido por programas que não leem CF_HDROP.</summary>
    private static readonly Lazy<uint> FileNameW = new(() => NativeMethods.RegisterClipboardFormat("FileNameW"));

    private const int OpenAttempts = 12;
    private const int OpenRetryDelayMs = 60;
    private const uint DropEffectCopy = 1;

    private readonly ILogger _log;

    public ClipboardWriter(ILogger log) => _log = log.ForContext<ClipboardWriter>();

    /// <summary>
    /// Publica o conteúdo.
    /// </summary>
    /// <param name="owner">
    /// Janela dona da área de transferência. Use a janela de mensagens do programa, que
    /// existe durante toda a execução.
    /// </param>
    /// <returns><see langword="true"/> quando tudo foi publicado.</returns>
    public bool Write(IntPtr owner, ClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var blocks = new List<(uint Format, IntPtr Memory)>(8);

        try
        {
            BuildBlocks(payload, blocks);

            if (blocks.Count == 0)
            {
                return false;
            }

            if (!OpenWithRetry(owner))
            {
                _log.Error("Não consegui abrir a área de transferência; outro programa está com ela.");
                return false;
            }

            try
            {
                if (!NativeMethods.EmptyClipboard())
                {
                    _log.Error("Não consegui esvaziar a área de transferência.");
                    return false;
                }

                foreach ((uint format, IntPtr memory) in blocks)
                {
                    if (NativeMethods.SetClipboardData(format, memory) == IntPtr.Zero)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                }

                // A partir daqui a memoria pertence ao sistema. Liberar qualquer bloco
                // agora corromperia a area de transferencia.
                blocks.Clear();
                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        }
        catch (Exception exception)
        {
            _log.Error(exception, "Falha ao publicar na área de transferência.");
            return false;
        }
        finally
        {
            // So sobra aqui o que NAO chegou a ser aceito pelo sistema.
            foreach ((uint _, IntPtr memory) in blocks)
            {
                NativeMethods.GlobalFree(memory);
            }
        }
    }

    /// <summary>
    /// Monta os blocos <b>na ordem em que serão publicados</b>.
    /// <para>
    /// A ordem é a regra inteira do produto. A documentação do Windows é explícita: os
    /// formatos são enumerados na ordem em que foram colocados, e o programa que cola
    /// costuma pegar o primeiro que reconhece. Por isso a imagem vem antes e o texto
    /// vai por último.
    /// </para>
    /// </summary>
    private static void BuildBlocks(ClipboardPayload payload, List<(uint, IntPtr)> blocks)
    {
        if (payload.Image is { } image)
        {
            // 1. PNG: o mais fiel, e o primeiro que o Chromium procura.
            blocks.Add((PngFormat.Value, Allocate(ImageWriter.EncodePng(image))));

            // 2. e 3. Bitmap com e sem canal alfa, para quem nao le PNG.
            //    CF_BITMAP nao entra: o sistema o sintetiza sozinho a partir destes.
            blocks.Add((NativeMethods.CF_DIBV5, Allocate(DibBuilder.BuildV5(image))));
            blocks.Add((NativeMethods.CF_DIB, Allocate(DibBuilder.BuildV3(image))));
        }

        if (payload.IncludeFileDrop && !string.IsNullOrWhiteSpace(payload.FilePath))
        {
            // 4. Arquivo. Desligado por padrao: faz o Outlook anexar em vez de embutir.
            blocks.Add((NativeMethods.CF_HDROP, AllocateDropFiles(payload.FilePath)));
            blocks.Add((PreferredDropEffect.Value, Allocate(BitConverter.GetBytes(DropEffectCopy))));
            blocks.Add((FileNameW.Value, AllocateUnicode(payload.FilePath)));
        }

        if (!string.IsNullOrEmpty(payload.Text))
        {
            // 5. Texto, sempre por ultimo: e o que sobra para quem nao entende imagem.
            blocks.Add((NativeMethods.CF_UNICODETEXT, AllocateUnicode(payload.Text)));
        }
    }

    /// <summary>
    /// Abre a área de transferência tentando algumas vezes.
    /// <para>
    /// Ela é um recurso exclusivo de todo o sistema: gerenciadores de área de
    /// transferência, sessões de área de trabalho remota e o Teams a abrem por
    /// milissegundos o tempo todo. Desistir na primeira tentativa transformaria uma
    /// disputa banal em captura perdida.
    /// </para>
    /// </summary>
    private bool OpenWithRetry(IntPtr owner)
    {
        for (int attempt = 1; attempt <= OpenAttempts; attempt++)
        {
            if (NativeMethods.OpenClipboard(owner))
            {
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            if (attempt == OpenAttempts)
            {
                _log.Warning(
                    "Área de transferência ocupada após {Tentativas} tentativas (erro {Erro}).",
                    OpenAttempts,
                    error);
                return false;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    private static IntPtr Allocate(ReadOnlySpan<byte> bytes)
    {
        IntPtr memory = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)bytes.Length);
        if (memory == IntPtr.Zero)
        {
            throw new OutOfMemoryException("Não consegui reservar memória para a área de transferência.");
        }

        IntPtr target = NativeMethods.GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(memory);
            throw new InvalidOperationException("Não consegui travar a memória da área de transferência.");
        }

        try
        {
            unsafe
            {
                bytes.CopyTo(new Span<byte>((void*)target, bytes.Length));
            }
        }
        finally
        {
            // GlobalUnlock devolve falso quando a contagem chega a zero, e isso NAO e
            // erro. Verificar o retorno aqui levaria a um diagnostico falso.
            NativeMethods.GlobalUnlock(memory);
        }

        return memory;
    }

    private static IntPtr AllocateUnicode(string text)
        => Allocate(Encoding.Unicode.GetBytes(text + "\0"));

    /// <summary>
    /// Monta o <c>CF_HDROP</c>: o cabeçalho de 20 bytes seguido da lista de caminhos em
    /// UTF-16, com <b>terminação nula dupla</b> — um nulo encerra cada caminho e outro
    /// encerra a lista.
    /// </summary>
    private static IntPtr AllocateDropFiles(string path)
    {
        int headerSize = Marshal.SizeOf<DROPFILES>();
        byte[] names = Encoding.Unicode.GetBytes(path + "\0\0");
        byte[] buffer = new byte[headerSize + names.Length];

        var header = new DROPFILES
        {
            FilesOffset = (uint)headerSize,
            Wide = 1,
        };

        MemoryMarshal.Write(buffer, in header);
        names.CopyTo(buffer, headerSize);

        return Allocate(buffer);
    }
}
