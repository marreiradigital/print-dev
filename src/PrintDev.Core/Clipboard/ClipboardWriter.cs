using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.Interop;
using PrintDev.Core.Paths;
using PrintDev.Core.Storage;
using Serilog;

namespace PrintDev.Core.Clipboard;

/// <summary>O que publicar na área de transferência.</summary>
/// <param name="Image">A captura. Nulo publica só o texto.</param>
/// <param name="Text">Texto do caminho. Nulo publica só a imagem.</param>
/// <param name="FilePath">Caminho real em disco, para o formato de arquivo.</param>
/// <param name="IncludeFileDrop">Publicar também o formato de arquivo.</param>
/// <param name="Png">
/// O PNG da imagem, quando já existe — o que acabou de ir para o disco, ou o próprio
/// arquivo. Vazio faz a imagem ser codificada aqui.
/// </param>
/// <param name="TextOnlyFor">
/// Quando há imagem e texto: os programas que recebem o texto. O texto é anunciado sem os
/// dados e só é entregue a quem estiver nesta lista; o resto fica com a imagem. Nulo
/// publica o texto para todo mundo.
/// </param>
public sealed record ClipboardPayload(
    CapturedImage? Image,
    string? Text,
    string? FilePath = null,
    bool IncludeFileDrop = false,
    ReadOnlyMemory<byte> Png = default,
    IReadOnlyCollection<string>? TextOnlyFor = null)
{
    /// <summary>
    /// Monta o que publicar de uma captura, conforme as configurações. Todo caminho que
    /// publica uma captura — a captura em si, recopiar do histórico — passa por aqui,
    /// para o mesmo conteúdo sair igual venha de onde vier.
    /// </summary>
    /// <param name="image">Os pixels. Nulo publica só o que não é imagem.</param>
    /// <param name="savedPath">Onde o arquivo está, se chegou ao disco.</param>
    /// <param name="png">O PNG já codificado, quando houver.</param>
    /// <param name="settings">As configurações da área de transferência.</param>
    /// <param name="content">O que publicar.</param>
    public static ClipboardPayload ForCapture(
        CapturedImage? image,
        string? savedPath,
        ReadOnlyMemory<byte> png,
        ClipboardSettings settings,
        ClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(settings);

        bool wantsImage = content is not ClipboardContent.Caminho;
        bool wantsText = content is not ClipboardContent.Imagem;

        // Sem arquivo em disco nao ha caminho para colar - mas a imagem ainda vai.
        // Perder o arquivo e ruim; perder a captura seria pior.
        string? text = wantsText && savedPath is not null
            ? PathTextFormatter.Format(savedPath, settings)
            : null;

        return new ClipboardPayload(
            Image: wantsImage ? image : null,
            Text: text,
            FilePath: savedPath,
            IncludeFileDrop: settings.IncludeFileDrop && savedPath is not null,
            Png: wantsImage ? png : default,
            TextOnlyFor: content == ClipboardContent.ImagemECaminho ? settings.TextDestinations : null);
    }
}

/// <summary>
/// Publica a captura em vários formatos numa única transação.
/// <para>
/// <b>É o coração do produto.</b> Nenhum programa pode saber para onde o usuário vai
/// colar. A imagem é publicada em vários formatos, na ordem certa, e o programa de
/// destino pega o primeiro que sabe consumir.
/// </para>
/// <para>
/// O caminho é diferente: ele vai <b>anunciado sem os dados</b> (renderização sob
/// demanda). Quando um programa pede o texto, o Windows pergunta a este programa, que
/// olha quem pediu e só entrega a terminal e IDE. Publicar imagem e texto juntos deixava a
/// escolha com o destino, e o histórico do Win+V e vários programas que aceitam imagem
/// escolhiam o texto — colavam o caminho no lugar da captura.
/// </para>
/// <para>
/// Tudo aqui roda na thread de interface, que é a da janela dona da área de
/// transferência: os pedidos de renderização chegam por mensagem a ela.
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

    /// <summary>
    /// Pede aos monitores da área de transferência — o histórico do Win+V inclusive — que
    /// ignorem esta publicação. Vai só na republicação feita depois de entregar o caminho,
    /// que não é uma cópia nova da pessoa.
    /// </summary>
    private static readonly Lazy<uint> ExcludeFromMonitors =
        new(() => NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"));

    /// <summary>Zero aqui deixa a publicação fora do histórico do Win+V.</summary>
    private static readonly Lazy<uint> CanIncludeInHistory =
        new(() => NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory"));

    private const int OpenAttempts = 12;
    private const int OpenRetryDelayMs = 60;
    private const uint DropEffectCopy = 1;

    private readonly ILogger _log;

    private IntPtr _owner;
    private string? _onDemandText;
    private IReadOnlyCollection<string>? _onDemandFor;
    private uint[] _published = [];

    /// <summary>
    /// Quem já teve a decisão registrada no log, nesta publicação. Navegador costuma ler
    /// a área de transferência sem parar — o Brave pede o texto a cada 0,8 s —, e cada
    /// pedido chega aqui. Registrar todos encheria o log de linhas iguais.
    /// </summary>
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

    public ClipboardWriter(ILogger log) => _log = log.ForContext<ClipboardWriter>();

    /// <summary>
    /// Liga este publicador à janela dona da área de transferência, por onde chegam os
    /// pedidos de renderização. Sem isto o caminho sob demanda nunca seria entregue.
    /// </summary>
    public void Attach(Hotkeys.HotkeyMessageWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.ClipboardFormatRequested += (_, format) => RenderOnDemand(format);
        window.ClipboardEmptied += (_, _) => ForgetOnDemand();
        window.ClipboardRearmRequested += (_, _) => Rearm();
    }

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

        // O caminho so vai sob demanda quando ha imagem para quem nao o receber. Texto
        // sozinho (o caminho pedido de proposito, uma cor, um link) vai para todo mundo.
        bool onDemand = payload.TextOnlyFor is not null
                        && payload.Image is not null
                        && !string.IsNullOrEmpty(payload.Text);

        var blocks = new List<(uint Format, IntPtr Memory)>(8);

        try
        {
            BuildBlocks(payload, blocks, includeText: !onDemand);

            if (blocks.Count == 0)
            {
                return false;
            }

            uint[] formats = [.. blocks.Select(block => block.Format)];

            if (!OpenWithRetry(owner))
            {
                _log.Error("Não consegui abrir a área de transferência; outro programa está com ela.");
                return false;
            }

            try
            {
                // Esvaziar avisa o dono anterior - que pode ser este mesmo programa, e
                // entao o caminho sob demanda da captura anterior e esquecido aqui.
                if (!NativeMethods.EmptyClipboard())
                {
                    _log.Error("Não consegui esvaziar a área de transferência.");
                    return false;
                }

                SetBlocks(blocks);

                if (onDemand)
                {
                    // Por ultimo, como sempre foi o texto: e o que sobra para quem nao
                    // entende imagem. Sem os dados - eles saem na hora do pedido.
                    NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, IntPtr.Zero);
                    _onDemandText = payload.Text;
                    _onDemandFor = payload.TextOnlyFor;
                }

                _owner = owner;
                _published = formats;
                _reported.Clear();
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
    /// Entrega os blocos ao sistema, tirando da lista cada um que ele aceitou.
    /// <para>
    /// Um bloco aceito pertence ao sistema dali em diante. Se um formato no meio falhar,
    /// só os que sobraram na lista podem ser liberados por quem chamou — liberar os já
    /// aceitos corromperia a área de transferência.
    /// </para>
    /// </summary>
    private static void SetBlocks(List<(uint Format, IntPtr Memory)> blocks)
    {
        while (blocks.Count > 0)
        {
            (uint format, IntPtr memory) = blocks[0];

            if (NativeMethods.SetClipboardData(format, memory) == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            blocks.RemoveAt(0);
        }
    }

    /// <summary>
    /// Alguém pediu um formato anunciado sem os dados. Se for o caminho, decide pelo
    /// programa que pediu.
    /// <para>
    /// Quem pediu é a janela que está com a área de transferência aberta neste instante.
    /// Programa que abre sem janela não tem como ser identificado assim; nesse caso vale a
    /// janela em primeiro plano, que numa colagem é onde a pessoa está colando.
    /// </para>
    /// <para>
    /// Recusar é simplesmente não entregar: o programa recebe o texto vazio e fica com a
    /// imagem, que já estava lá.
    /// </para>
    /// </summary>
    private void RenderOnDemand(uint format)
    {
        if (format != NativeMethods.CF_UNICODETEXT || _onDemandText is not { } text || _onDemandFor is not { } allowed)
        {
            return;
        }

        IntPtr requester = NativeMethods.GetOpenClipboardWindow();
        if (requester == IntPtr.Zero)
        {
            requester = NativeMethods.GetForegroundWindow();
        }

        string process = ActiveWindowInfo.ReadProcessName(requester);
        bool accepted = TextDestination.Accepts(process, allowed);
        bool firstTime = _reported.Add((accepted ? "+" : "-") + process);

        if (!accepted)
        {
            if (firstTime)
            {
                _log.Information("Caminho não entregue a {Processo}: ele recebe a imagem", process);
            }

            return;
        }

        IntPtr memory = AllocateUnicode(text);
        if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, memory) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(memory);
            _log.Warning("Não consegui entregar o caminho a {Processo}", process);
            return;
        }

        if (firstTime)
        {
            _log.Information("Caminho entregue a {Processo}", process);
        }

        // Entregue, o texto passaria a valer para todo mundo - inclusive para o programa
        // de imagem onde a pessoa colar em seguida. Republicar logo depois devolve o
        // caminho ao estado sob demanda. Por mensagem: quem pediu ainda esta com a area
        // aberta, e so a solta quando esta chamada voltar.
        NativeMethods.PostMessage(_owner, NativeMethods.WM_APP_REARM_CLIPBOARD, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>A área foi esvaziada: o que estava nela deixou de ser nosso.</summary>
    private void ForgetOnDemand()
    {
        _onDemandText = null;
        _onDemandFor = null;
        _published = [];
    }

    /// <summary>
    /// Republica a captura com o caminho de novo sob demanda, depois de entregá-lo a um
    /// terminal ou IDE.
    /// <para>
    /// Os blocos da imagem são copiados da própria área de transferência — eles ainda
    /// são nossos — e a republicação vai marcada para o histórico do Win+V e os monitores
    /// a ignorarem: não é uma cópia nova da pessoa.
    /// </para>
    /// </summary>
    private void Rearm()
    {
        string? text = _onDemandText;
        IReadOnlyCollection<string>? allowed = _onDemandFor;
        uint[] formats = _published;

        if (text is null || allowed is null || formats.Length == 0 || !OpenWithRetry(_owner))
        {
            return;
        }

        var copies = new List<(uint Format, IntPtr Memory)>(formats.Length + 2);

        try
        {
            // Outro programa pode ter copiado algo nesse meio tempo; o que e dele fica.
            if (NativeMethods.GetClipboardOwner() != _owner)
            {
                return;
            }

            foreach (uint format in formats)
            {
                IntPtr current = NativeMethods.GetClipboardData(format);
                if (current != IntPtr.Zero)
                {
                    copies.Add((format, Duplicate(current)));
                }
            }

            if (copies.Count == 0)
            {
                return;
            }

            copies.Add((ExcludeFromMonitors.Value, Allocate(BitConverter.GetBytes(0u))));
            copies.Add((CanIncludeInHistory.Value, Allocate(BitConverter.GetBytes(0u))));

            if (!NativeMethods.EmptyClipboard())
            {
                return;
            }

            SetBlocks(copies);
            NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, IntPtr.Zero);

            _onDemandText = text;
            _onDemandFor = allowed;
            _published = formats;
            _log.Debug("Caminho de volta ao modo sob demanda");
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Não consegui devolver o caminho ao modo sob demanda.");
        }
        finally
        {
            NativeMethods.CloseClipboard();

            foreach ((uint _, IntPtr memory) in copies)
            {
                NativeMethods.GlobalFree(memory);
            }
        }
    }

    /// <summary>Copia um bloco de memória global para um novo.</summary>
    private static IntPtr Duplicate(IntPtr source)
    {
        int size = checked((int)NativeMethods.GlobalSize(source));
        IntPtr from = NativeMethods.GlobalLock(source);

        if (from == IntPtr.Zero)
        {
            throw new InvalidOperationException("Não consegui ler um bloco da área de transferência.");
        }

        try
        {
            unsafe
            {
                return Allocate(new ReadOnlySpan<byte>((void*)from, size));
            }
        }
        finally
        {
            NativeMethods.GlobalUnlock(source);
        }
    }

    /// <summary>
    /// Limpa a área de transferência, mas <b>só se o conteúdo dela ainda for nosso</b>.
    /// <para>
    /// É o que fecha o "Desfazer": sem isso, o arquivo vai para a Lixeira e o caminho
    /// dele continua colável — a próxima colagem num terminal entrega um caminho morto.
    /// </para>
    /// <para>
    /// A conferência de dono não é zelo excessivo: entre a captura e o arrependimento a
    /// pessoa pode ter copiado outra coisa, e apagar o que ela copiou depois seria um
    /// estrago bem maior do que o que se está desfazendo.
    /// </para>
    /// </summary>
    /// <returns>
    /// <see langword="true"/> quando limpou. <see langword="false"/> quando o conteúdo já
    /// era de outro programa — que também é um desfecho correto, e não um erro.
    /// </returns>
    public bool ClearIfOwned(IntPtr owner)
    {
        if (owner == IntPtr.Zero || NativeMethods.GetClipboardOwner() != owner)
        {
            return false;
        }

        if (!OpenWithRetry(owner))
        {
            _log.Warning("Não consegui abrir a área de transferência para limpá-la.");
            return false;
        }

        try
        {
            // Entre a checagem de dono e o Open outro programa pode ter assumido; com a
            // area aberta, a resposta e definitiva.
            if (NativeMethods.GetClipboardOwner() != owner)
            {
                return false;
            }

            if (!NativeMethods.EmptyClipboard())
            {
                _log.Warning("Não consegui esvaziar a área de transferência.");
                return false;
            }

            _log.Debug("Área de transferência limpa: o conteúdo era nosso.");
            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
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
    private static void BuildBlocks(ClipboardPayload payload, List<(uint, IntPtr)> blocks, bool includeText)
    {
        if (payload.Image is { } image)
        {
            // 1. PNG: o mais fiel, e o primeiro que o Chromium procura. Reaproveita o
            //    que ja foi codificado para o disco, quando houver.
            ReadOnlyMemory<byte> png = payload.Png.IsEmpty ? ImageWriter.EncodePng(image) : payload.Png;
            blocks.Add((PngFormat.Value, Allocate(png.Span)));

            // 2. e 3. Bitmap com e sem canal alfa, para quem nao le PNG. Escritos direto
            //    na memoria que vai para o sistema, sem um array intermediario do
            //    tamanho da captura.
            //    CF_BITMAP nao entra: o sistema o sintetiza sozinho a partir destes.
            blocks.Add((NativeMethods.CF_DIBV5, Allocate(DibBuilder.SizeV5(image), image, DibBuilder.WriteV5)));
            blocks.Add((NativeMethods.CF_DIB, Allocate(DibBuilder.SizeV3(image), image, DibBuilder.WriteV3)));
        }

        if (payload.IncludeFileDrop && !string.IsNullOrWhiteSpace(payload.FilePath))
        {
            // 4. Arquivo. Desligado por padrao: faz o Outlook anexar em vez de embutir.
            blocks.Add((NativeMethods.CF_HDROP, AllocateDropFiles(payload.FilePath)));
            blocks.Add((PreferredDropEffect.Value, Allocate(BitConverter.GetBytes(DropEffectCopy))));
            blocks.Add((FileNameW.Value, AllocateUnicode(payload.FilePath)));
        }

        if (includeText && !string.IsNullOrEmpty(payload.Text))
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
                // O acesso negado é o caso esperado — outro programa está com a área
                // aberta — e merece nome, e não um número que ninguém vai procurar.
                string causa = error == NativeMethods.ERROR_ACCESS_DENIED
                    ? "outro programa está com ela aberta"
                    : $"erro {error}";

                _log.Warning(
                    "Área de transferência ocupada após {Tentativas} tentativas: {Causa}.",
                    OpenAttempts,
                    causa);
                return false;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    private static IntPtr Allocate(ReadOnlySpan<byte> bytes)
    {
        IntPtr memory = AllocateRaw(bytes.Length, out IntPtr target);

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

    /// <summary>
    /// Reserva o bloco e deixa quem sabe o formato escrever direto nele.
    /// </summary>
    private static IntPtr Allocate(int size, CapturedImage image, SpanAction<byte, CapturedImage> write)
    {
        IntPtr memory = AllocateRaw(size, out IntPtr target);

        try
        {
            unsafe
            {
                write(new Span<byte>((void*)target, size), image);
            }
        }
        catch
        {
            NativeMethods.GlobalUnlock(memory);
            NativeMethods.GlobalFree(memory);
            throw;
        }

        NativeMethods.GlobalUnlock(memory);
        return memory;
    }

    /// <summary>Reserva e trava um bloco móvel. Quem chama destrava.</summary>
    private static IntPtr AllocateRaw(int size, out IntPtr target)
    {
        IntPtr memory = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)size);
        if (memory == IntPtr.Zero)
        {
            throw new OutOfMemoryException("Não consegui reservar memória para a área de transferência.");
        }

        target = NativeMethods.GlobalLock(memory);
        if (target == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(memory);
            throw new InvalidOperationException("Não consegui travar a memória da área de transferência.");
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
