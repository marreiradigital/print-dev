using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.Interop;
using PrintDev.Core.Paths;
using PrintDev.Core.Storage;
using Serilog;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

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
/// <b>Com o histórico do Win+V ligado, a captura sai em duas etapas.</b> O histórico
/// descarta o item inteiro quando um formato anunciado não é entregue — e o caminho não
/// pode ser entregue a ele, senão o Win+V mostra o texto no lugar da miniatura. Então a
/// primeira publicação leva só a imagem (o histórico guarda a miniatura) e, assim que o
/// histórico avisa que registrou, a captura é republicada com o caminho sob demanda,
/// marcada para o histórico ignorar. Medido em 27/11/2026: o histórico lê de 0,2 a 0,5 s
/// depois da publicação; republicar antes disso fazia o item sumir.
/// </para>
/// <para>
/// Tudo aqui roda na thread de interface, que é a da janela dona da área de
/// transferência: os pedidos de renderização chegam por mensagem a ela.
/// </para>
/// </summary>
public sealed class ClipboardWriter : IDisposable
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
    /// ignorem esta publicação. Vai só nas republicações que anunciam o caminho, que não
    /// são uma cópia nova da pessoa.
    /// </summary>
    private static readonly Lazy<uint> ExcludeFromMonitors =
        new(() => NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"));

    /// <summary>Zero aqui deixa a publicação fora do histórico do Win+V.</summary>
    private static readonly Lazy<uint> CanIncludeInHistory =
        new(() => NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory"));

    private const int OpenBudgetMs = 1000;
    private const int OpenRetryDelayMs = 50;
    private const uint DropEffectCopy = 1;

    /// <summary>
    /// Quanto esperar pelo aviso do histórico antes de anunciar o caminho mesmo assim. O
    /// histórico pode recusar o item (ou estar lento), e o terminal não pode ficar sem o
    /// caminho por causa disso.
    /// </summary>
    private static readonly TimeSpan HistoryWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Folga na comparação do horário do item do histórico com o da publicação: o
    /// horário do item é o da leitura, que vem depois, mas os dois relógios não são o
    /// mesmo.
    /// </summary>
    private static readonly TimeSpan HistoryClockSlack = TimeSpan.FromMilliseconds(100);

    private readonly ILogger _log;

    private IntPtr _owner;

    /// <summary>
    /// Conta as publicações. Todo trabalho agendado (anunciar o caminho, republicar depois
    /// de entregar) leva o número da publicação a que se refere e é descartado se outra
    /// já tomou o lugar dela — senão uma republicação atrasada da captura anterior
    /// marcaria a nova para o histórico ignorar antes de ele registrá-la.
    /// </summary>
    private int _generation;

    private string? _onDemandText;
    private IReadOnlyCollection<string>? _onDemandFor;
    private uint[] _published = [];

    /// <summary>A captura está publicada só como imagem, esperando o histórico registrá-la.</summary>
    private bool _awaitingHistory;
    private DateTimeOffset _publishedAt;
    private Stopwatch _sincePublished = new();

    private Dispatcher? _dispatcher;
    private DispatcherTimer? _historyTimeout;
    private bool _historySubscribed;
    private int _announceRetries;

    /// <summary>
    /// O caminho foi entregue e ficou valendo para todos; falta devolvê-lo ao modo sob
    /// demanda. Fica pendente enquanto um terminal ou IDE estiver em primeiro plano.
    /// </summary>
    private bool _rearmPending;
    private NativeMethods.WinEventProc? _foregroundCallback;
    private IntPtr _foregroundHook;

    private const int MaxAnnounceRetries = 4;
    private static readonly TimeSpan AnnounceRetryDelay = TimeSpan.FromMilliseconds(250);

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
        window.ClipboardRearmRequested += (_, generation) => RearmAfterDelivery(generation);

        _dispatcher = Dispatcher.CurrentDispatcher;
        _historyTimeout = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = HistoryWait };
        _historyTimeout.Tick += (_, _) => AnnounceText(_generation, "o histórico não avisou a tempo");

        try
        {
            WinClipboard.HistoryChanged += OnHistoryChanged;
            _historySubscribed = true;
        }
        catch (Exception exception)
        {
            // Sem o aviso, o tempo-limite anuncia o caminho: a miniatura no Win+V pode
            // falhar, a colagem nao.
            _log.Warning(exception, "Não consegui acompanhar o histórico do Win+V.");
        }

        _foregroundCallback = (_, _, _, _, _, _, _) => RearmIfLeftDestination();
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundCallback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _historyTimeout?.Stop();

        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_historySubscribed)
        {
            try
            {
                WinClipboard.HistoryChanged -= OnHistoryChanged;
            }
            catch (Exception)
            {
                // Encerrando: nao ha o que fazer.
            }

            _historySubscribed = false;
        }
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

        // Com o historico ligado, o caminho so e anunciado depois que ele registrar a
        // imagem. Anunciado agora, o historico pediria o texto, receberia nada e jogaria
        // fora a captura inteira.
        bool historyFirst = onDemand && IsHistoryEnabled();

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

                if (onDemand && !historyFirst)
                {
                    // Por ultimo, como sempre foi o texto: e o que sobra para quem nao
                    // entende imagem. Sem os dados - eles saem na hora do pedido.
                    NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, IntPtr.Zero);
                }

                if (onDemand)
                {
                    _onDemandText = payload.Text;
                    _onDemandFor = payload.TextOnlyFor;
                }

                _generation++;
                _owner = owner;
                _published = formats;
                _awaitingHistory = historyFirst;
                _publishedAt = DateTimeOffset.Now;
                _sincePublished = Stopwatch.StartNew();
                _reported.Clear();
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            if (historyFirst && _historyTimeout is not null)
            {
                _announceRetries = 0;
                _historyTimeout.Stop();
                _historyTimeout.Interval = HistoryWait;
                _historyTimeout.Start();
            }

            return true;
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
        _rearmPending = true;
        NativeMethods.PostMessage(
            _owner,
            NativeMethods.WM_APP_REARM_CLIPBOARD,
            new IntPtr(_generation),
            IntPtr.Zero);
    }

    /// <summary>A área foi esvaziada: o que estava nela deixou de ser nosso.</summary>
    private void ForgetOnDemand()
    {
        _onDemandText = null;
        _onDemandFor = null;
        _published = [];
        _awaitingHistory = false;
        _rearmPending = false;
        _historyTimeout?.Stop();
    }

    /// <summary>
    /// O histórico do Win+V mudou. Se foi a captura que está esperando entrar nele,
    /// é hora de anunciar o caminho.
    /// <para>
    /// O aviso chega numa thread qualquer e não diz o que mudou: a conferência — o item
    /// mais novo do histórico é posterior a esta publicação? — evita que o registro da
    /// captura anterior, chegando atrasado, faça anunciar o caminho da atual antes de o
    /// histórico registrá-la.
    /// </para>
    /// </summary>
    private void OnHistoryChanged(object? sender, object e)
        => _dispatcher?.BeginInvoke(new Action(() => _ = CheckHistoryAsync(_generation)));

    private async Task CheckHistoryAsync(int generation)
    {
        if (!_awaitingHistory || generation != _generation)
        {
            return;
        }

        try
        {
            var history = await WinClipboard.GetHistoryItemsAsync();
            DateTimeOffset newest = history.Items.Count > 0 ? history.Items[0].Timestamp : DateTimeOffset.MinValue;

            if (newest < _publishedAt - HistoryClockSlack)
            {
                return;
            }
        }
        catch (Exception exception)
        {
            // Sem como conferir, vale o aviso: o historico mudou depois da publicacao.
            _log.Debug(exception, "Não consegui ler o histórico do Win+V.");
        }

        AnnounceText(generation, "o histórico registrou a imagem");
    }

    /// <summary>
    /// Segunda etapa: a imagem já está no histórico, e o caminho passa a ser anunciado
    /// para terminal e IDE.
    /// </summary>
    private void AnnounceText(int generation, string reason)
    {
        _historyTimeout?.Stop();

        if (!_awaitingHistory || generation != _generation)
        {
            return;
        }

        _awaitingHistory = false;

        if (Republish(generation))
        {
            _log.Debug(
                "Caminho anunciado {Milissegundos} ms depois da captura: {Motivo}",
                _sincePublished.ElapsedMilliseconds,
                reason);
            return;
        }

        // Area ocupada por tempo demais: tentar de novo logo, enquanto a captura ainda
        // for a atual e ainda for nossa. Sem isso o terminal ficaria sem o caminho.
        if (_onDemandText is not null
            && NativeMethods.GetClipboardOwner() == _owner
            && _announceRetries < MaxAnnounceRetries)
        {
            _announceRetries++;
            _awaitingHistory = true;
            _historyTimeout!.Interval = AnnounceRetryDelay;
            _historyTimeout.Start();
        }
    }

    /// <summary>Lê se o histórico do Win+V está ligado. Na dúvida, não está.</summary>
    private bool IsHistoryEnabled()
    {
        if (_dispatcher is null)
        {
            return false;
        }

        try
        {
            return WinClipboard.IsHistoryEnabled();
        }
        catch (Exception exception)
        {
            _log.Debug(exception, "Não consegui ler se o histórico do Win+V está ligado.");
            return false;
        }
    }

    /// <summary>
    /// Depois de entregar o caminho, devolve-o ao modo sob demanda — se a publicação a
    /// que o pedido se refere ainda for a atual.
    /// <para>
    /// <b>Não enquanto um terminal ou IDE estiver em primeiro plano.</b> Ali o caminho é
    /// o que a pessoa quer colar, e republicar a cada leitura custava caro: o Brave lê a
    /// área a cada 0,8 s sem janela, a leitura caía na conta do terminal em primeiro plano,
    /// e cada uma copiava a imagem inteira de novo e acordava todo programa que vigia a
    /// área de transferência. A republicação fica para quando o primeiro plano sair da
    /// lista — antes de a pessoa colar num programa de imagem.
    /// </para>
    /// </summary>
    private void RearmAfterDelivery(int generation)
    {
        if (generation != _generation || !_rearmPending || ForegroundTakesText())
        {
            return;
        }

        _rearmPending = false;

        if (Republish(generation))
        {
            _log.Debug("Caminho de volta ao modo sob demanda");
        }
    }

    /// <summary>A janela em primeiro plano mudou: se saiu de terminal e IDE, é a hora.</summary>
    private void RearmIfLeftDestination()
    {
        if (_rearmPending)
        {
            RearmAfterDelivery(_generation);
        }
    }

    /// <summary>O programa em primeiro plano está entre os que recebem o caminho.</summary>
    private bool ForegroundTakesText()
        => _onDemandFor is { } allowed
           && TextDestination.Accepts(
               ActiveWindowInfo.ReadProcessName(NativeMethods.GetForegroundWindow()),
               allowed);

    /// <summary>
    /// Republica a captura com o caminho sob demanda.
    /// <para>
    /// Os blocos da imagem são copiados da própria área de transferência — eles ainda
    /// são nossos — e a republicação vai marcada para o histórico do Win+V e os monitores
    /// a ignorarem: não é uma cópia nova da pessoa.
    /// </para>
    /// </summary>
    /// <returns><see langword="true"/> quando republicou.</returns>
    private bool Republish(int generation)
    {
        string? text = _onDemandText;
        IReadOnlyCollection<string>? allowed = _onDemandFor;
        uint[] formats = _published;

        if (text is null || allowed is null || formats.Length == 0 || !OpenWithRetry(_owner))
        {
            return false;
        }

        var copies = new List<(uint Format, IntPtr Memory)>(formats.Length + 2);

        try
        {
            // Outro programa pode ter copiado algo nesse meio tempo; o que e dele fica.
            if (NativeMethods.GetClipboardOwner() != _owner || generation != _generation)
            {
                return false;
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
                return false;
            }

            copies.Add((ExcludeFromMonitors.Value, Allocate(BitConverter.GetBytes(0u))));
            copies.Add((CanIncludeInHistory.Value, Allocate(BitConverter.GetBytes(0u))));

            // Esvaziar avisa este mesmo programa (WM_DESTROYCLIPBOARD), que esquece o
            // estado - por isso ele foi guardado antes e volta logo abaixo.
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            SetBlocks(copies);
            NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, IntPtr.Zero);

            _onDemandText = text;
            _onDemandFor = allowed;
            _published = formats;
            return true;
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Não consegui pôr o caminho no modo sob demanda.");
            return false;
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
    /// Abre a área de transferência tentando por até <see cref="OpenBudgetMs"/>.
    /// <para>
    /// Ela é um recurso exclusivo de todo o sistema: gerenciadores de área de
    /// transferência, sessões de área de trabalho remota e o Teams a abrem por
    /// milissegundos o tempo todo. Desistir na primeira tentativa transformaria uma
    /// disputa banal em captura perdida.
    /// </para>
    /// <para>
    /// <b>A espera continua atendendo os pedidos do caminho sob demanda.</b> Quem pede um
    /// formato anunciado sem os dados fica com a área aberta, parado, até esta thread
    /// responder. Se ela dormisse esperando a área ficar livre, os dois esperariam um pelo
    /// outro até o tempo acabar — e a captura nova não chegava à área de transferência.
    /// Era isso que fazia falhar a colagem de prints seguidos: um navegador lendo a área
    /// enquanto a captura anterior ainda estava nela.
    /// </para>
    /// </summary>
    private bool OpenWithRetry(IntPtr owner)
    {
        long deadline = Environment.TickCount64 + OpenBudgetMs;
        int attempts = 0;

        while (true)
        {
            attempts++;

            if (NativeMethods.OpenClipboard(owner))
            {
                return true;
            }

            int error = Marshal.GetLastWin32Error();
            long remaining = deadline - Environment.TickCount64;

            if (remaining <= 0)
            {
                // O acesso negado é o caso esperado — outro programa está com a área
                // aberta — e merece nome, e não um número que ninguém vai procurar.
                string causa = error == NativeMethods.ERROR_ACCESS_DENIED
                    ? "outro programa está com ela aberta"
                    : $"erro {error}";

                _log.Warning(
                    "Área de transferência ocupada após {Tentativas} tentativas: {Causa}.",
                    attempts,
                    causa);
                return false;
            }

            WaitAnsweringRequests((int)Math.Min(OpenRetryDelayMs, remaining));
        }
    }

    /// <summary>
    /// Espera sem deixar de responder quem está parado esperando esta thread.
    /// <para>
    /// Atende só as mensagens <b>enviadas</b> — é por elas que chegam os pedidos de
    /// renderização. As postadas (atalhos, cliques, timers) ficam na fila para depois: tratá-las
    /// aqui seria rodar outra captura no meio desta.
    /// </para>
    /// </summary>
    private static void WaitAnsweringRequests(int milliseconds)
    {
        long deadline = Environment.TickCount64 + milliseconds;

        while (true)
        {
            NativeMethods.PeekMessage(
                out _,
                IntPtr.Zero,
                0,
                0,
                NativeMethods.PM_NOREMOVE | NativeMethods.PM_QS_SENDMESSAGE);

            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                return;
            }

            NativeMethods.MsgWaitForMultipleObjectsEx(
                0,
                IntPtr.Zero,
                (uint)remaining,
                NativeMethods.QS_SENDMESSAGE,
                NativeMethods.MWMO_INPUTAVAILABLE);
        }
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
