using PrintDev.Core.Clipboard;
using PrintDev.Core.Cloud;
using PrintDev.Core.Configuration;
using PrintDev.Core.History;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Paths;
using PrintDev.Core.Runtime;
using PrintDev.Core.Screens;
using PrintDev.Core.Storage;
using Serilog;

namespace PrintDev.Notifications;

/// <summary>
/// Mostra os avisos de captura e cuida do empilhamento deles.
/// </summary>
public sealed class ToastHost
{
    /// <summary>
    /// Quantos avisos podem ficar visíveis ao mesmo tempo. Passar disso vira uma parede
    /// que cobre o canto da tela — e quem capturou dez vezes seguidas não está lendo
    /// aviso nenhum.
    /// </summary>
    private const int MaxVisible = 3;

    /// <summary>
    /// Quanto o aviso fica na tela depois de responder algo que precisa ser lido.
    /// Mais longo que a vida normal do aviso: um link e um motivo de erro levam mais
    /// tempo para ler do que "captura salva".
    /// </summary>
    private static readonly TimeSpan AvisoDeLeitura = TimeSpan.FromSeconds(8);

    private const int EdgeMargin = 24;
    private const int StackGap = 8;

    private readonly ISettingsService _settings;
    private readonly ClipboardWriter _clipboard;
    private readonly HotkeyMessageWindow _messageWindow;
    private readonly CaptureUndoService _undo;
    private readonly CloudUploader _cloud;
    private readonly CloudLinkStore _links;
    private readonly ILogger _log;
    private readonly List<ToastWindow> _visible = [];

    /// <summary>
    /// Arquivos sendo enviados agora. Clique duplo no botão, ou o atalho apertado com o
    /// envio em andamento, subiam a mesma imagem duas vezes e criavam dois links.
    /// </summary>
    private readonly HashSet<string> _enviando = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pedido de anotação vindo do aviso.
    /// <para>
    /// É um evento, e não uma chamada direta ao coordenador de captura: o coordenador já
    /// depende deste host para mostrar o aviso, e a chamada de volta fecharia um ciclo
    /// que o container recusaria montar.
    /// </para>
    /// </summary>
    public event EventHandler<CaptureHistoryItem>? AnnotateRequested;

    /// <summary>Pedido de fixar na tela, pelo mesmo motivo do evento acima.</summary>
    public event EventHandler<CaptureHistoryItem>? PinRequested;

    public ToastHost(
        ISettingsService settings,
        ClipboardWriter clipboard,
        HotkeyMessageWindow messageWindow,
        CaptureUndoService undo,
        CloudUploader cloud,
        CloudLinkStore links,
        ILogger log)
    {
        _settings = settings;
        _clipboard = clipboard;
        _messageWindow = messageWindow;
        _undo = undo;
        _cloud = cloud;
        _links = links;
        _log = log.ForContext<ToastHost>();

        // Desfeita pelo atalho ou pela bandeja, o aviso dela nao pode continuar na tela
        // oferecendo Anotar, Fixar e Copiar caminho de um arquivo que foi para a Lixeira.
        _undo.Undone += (_, item) =>
        {
            foreach (ToastWindow toast in _visible.Where(t => SameCapture(t.Item, item)).ToList())
            {
                toast.Dismiss();
            }
        };
    }

    /// <summary>
    /// Avisa que uma captura aconteceu.
    /// </summary>
    /// <param name="item">A captura.</param>
    /// <param name="near">
    /// Onde ela aconteceu. O aviso aparece no monitor que contém este retângulo, e não
    /// sempre na tela principal: quem capturou no monitor secundário está olhando para
    /// ele.
    /// </param>
    /// <param name="copied">
    /// Falso quando a captura devia ir para a área de transferência e não foi. O aviso diz
    /// isso em vez de "Captura salva", e espera a nova tentativa.
    /// </param>
    /// <param name="force">
    /// Mostra mesmo com o aviso desligado nas configurações. Só para quando o aviso é o
    /// único lugar onde a resposta de uma ação pedida aparece — o envio para a nuvem.
    /// </param>
    public void Show(CaptureHistoryItem item, PixelRect near, bool copied = true, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(item);

        CaptureSettings capture = _settings.Current.Capture;
        if (!capture.ShowToast && !force)
        {
            return;
        }

        while (_visible.Count >= MaxVisible)
        {
            // Sai o mais antigo que nao esta esperando nada. Derrubar um aviso no meio de
            // um envio deixava o link chegar sem ninguem para mostra-lo.
            ToastWindow oldest = _visible.FirstOrDefault(t => !t.IsHeld) ?? _visible[0];
            oldest.Dismiss();
            _visible.Remove(oldest);
        }

        var toast = new ToastWindow(item, TimeSpan.FromSeconds(Math.Clamp(capture.ToastSeconds, 1, 30)));
        toast.ActionChosen += (sender, action) => Handle((ToastWindow)sender!, action);
        toast.Closed += (sender, _) =>
        {
            _visible.Remove((ToastWindow)sender!);
            Restack(near);

            // Último aviso fora da tela significa que o trabalho terminou e o programa
            // volta a dormir na bandeja. Uma captura de tela cheia deixa dezenas de
            // megabytes de bitmap para trás, e não há motivo para segurá-los até a
            // próxima vez — que pode ser daqui a horas.
            //
            // Aqui, e não num temporizador: uma rajada de capturas seguidas colapsa num
            // corte só, quando a última some.
            if (_visible.Count == 0)
            {
                WorkingSet.ReleaseSoon();
            }
        };

        _visible.Add(toast);
        toast.ShowAt(SlotFor(near, _visible.Count - 1, toast.MeasuredHeight));

        if (!copied)
        {
            toast.ShowNotCopied();
        }
    }

    /// <summary>Mostra no aviso da captura o desfecho da nova tentativa de copiá-la.</summary>
    public void ShowCopyRetried(CaptureHistoryItem item, bool copied)
    {
        ArgumentNullException.ThrowIfNull(item);

        foreach (ToastWindow toast in _visible.Where(t => SameCapture(t.Item, item)))
        {
            toast.ShowCopyRetried(copied, copied ? TimeSpan.FromSeconds(Math.Clamp(_settings.Current.Capture.ToastSeconds, 1, 30)) : AvisoDeLeitura);
        }
    }

    /// <summary>
    /// Calcula onde o aviso de índice indicado fica, empilhando de baixo para cima.
    /// </summary>
    private static PixelRect SlotFor(PixelRect near, int index, double height)
    {
        IReadOnlyList<MonitorInfo> monitors = VirtualDesktop.Enumerate();
        (int centerX, int centerY) = near.Center;
        MonitorInfo? monitor = VirtualDesktop.NearestMonitor(monitors, centerX, centerY)
                               ?? monitors.FirstOrDefault(m => m.IsPrimary);

        PixelRect area = monitor?.WorkArea ?? VirtualDesktop.BoundingBox();

        // A janela e medida em unidades independentes de dispositivo; o posicionamento e
        // em pixels fisicos. Sem a multiplicacao pela escala, o aviso sairia com o
        // tamanho errado num monitor com escala diferente de 100%.
        double scaleX = monitor?.ScaleX ?? 1;
        double scaleY = monitor?.ScaleY ?? 1;

        int physicalWidth = (int)Math.Round(ToastWindow.WidthDip * scaleX);
        int physicalHeight = (int)Math.Round(height * scaleY);
        int margin = (int)Math.Round(EdgeMargin * scaleX);
        int gap = (int)Math.Round(StackGap * scaleY);

        int left = area.Right - physicalWidth - margin;
        int top = area.Bottom - margin - ((physicalHeight + gap) * (index + 1));

        return new PixelRect(left, top, physicalWidth, physicalHeight);
    }

    private static bool SameCapture(CaptureHistoryItem a, CaptureHistoryItem b)
        => ReferenceEquals(a, b)
           || (a.Path is not null && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

    private void Restack(PixelRect near)
    {
        for (int index = 0; index < _visible.Count; index++)
        {
            _visible[index].MoveTo(SlotFor(near, index, _visible[index].MeasuredHeight));
        }
    }

    private void Handle(ToastWindow toast, ToastAction action)
    {
        CaptureHistoryItem item = toast.Item;

        // Desfazer é a única ação que faz sentido sem arquivo: sobra tirar do histórico e
        // devolver a área de transferência.
        if (action == ToastAction.Undo)
        {
            UndoOutcome resultado = _undo.Undo(item);

            if (resultado.Success)
            {
                toast.Dismiss();
            }
            else
            {
                toast.ShowFailure(resultado.Message);
            }

            return;
        }

        // Enviar tambem nao pode descartar o aviso: o envio demora, e a resposta -- o
        // link, ou o motivo da falha -- precisa aparecer onde a pessoa clicou.
        if (action == ToastAction.Cloud)
        {
            _ = EnviarParaNuvem(toast, item);
            return;
        }

        if (item.Path is null)
        {
            toast.Dismiss();
            return;
        }

        toast.Dismiss();

        switch (action)
        {
            case ToastAction.Annotate:
                AnnotateRequested?.Invoke(this, item);
                break;

            case ToastAction.Pin:
                PinRequested?.Invoke(this, item);
                break;

            case ToastAction.CopyPath:
                CopyPath(item.Path);
                break;

            case ToastAction.Open:
                if (!ShellOpen.File(item.Path))
                {
                    _log.Warning("Não consegui abrir {Arquivo}", item.Path);
                }

                break;
        }
    }

    /// <summary>
    /// Envia a captura indicada, vinda do atalho global.
    /// <para>
    /// Se o aviso dela ainda estiver na tela, reaproveita aquele — dois avisos da
    /// mesma captura, um enviando e outro parado, seria confuso. Senão, abre um só
    /// para ter onde mostrar o link ou o motivo da falha.
    /// </para>
    /// </summary>
    public void SendToCloud(CaptureHistoryItem? item, PixelRect near)
    {
        if (item is null)
        {
            _log.Information("Atalho de envio acionado sem captura recente.");
            return;
        }

        ToastWindow? naTela = _visible.LastOrDefault(t => ReferenceEquals(t.Item, item));

        if (naTela is not null)
        {
            _ = EnviarParaNuvem(naTela, item);
            return;
        }

        // Com o aviso desligado, nao haveria onde mostrar o link nem o motivo de uma falha:
        // o atalho e o botao Nuvem do seletor simplesmente nao faziam nada.
        Show(item, near, force: true);

        ToastWindow? recem = _visible.LastOrDefault(t => ReferenceEquals(t.Item, item));

        if (recem is not null)
        {
            _ = EnviarParaNuvem(recem, item);
        }
    }

    /// <summary>
    /// Publica a captura e devolve um link temporário.
    /// <para>
    /// É a única ação do aviso que faz um arquivo do usuário sair da máquina, então
    /// ela pergunta antes — uma vez — e nunca acontece sem clique.
    /// </para>
    /// </summary>
    private async Task EnviarParaNuvem(ToastWindow toast, CaptureHistoryItem item)
    {
        if (item.Path is null)
        {
            toast.ShowCloudResult("Esta captura não chegou ao disco, então não há o que enviar.", false, AvisoDeLeitura);
            return;
        }

        if (_enviando.Contains(item.Path))
        {
            _log.Debug("Envio de {Arquivo} já em andamento; ignorando o pedido repetido", item.Path);
            return;
        }

        try
        {
            _enviando.Add(item.Path);
            await Enviar(toast, item.Path);
        }
        catch (Exception excecao)
        {
            // A tarefa e disparada sem ninguem esperando por ela: uma excecao aqui
            // sumiria e deixaria o aviso parado em "Enviando" para sempre.
            _log.Error(excecao, "Falha inesperada no envio para a nuvem");
            toast.ShowCloudResult("O envio falhou por um erro inesperado.", false, AvisoDeLeitura);
        }
        finally
        {
            _enviando.Remove(item.Path);
        }
    }

    private async Task Enviar(ToastWindow toast, string path)
    {

        CloudSettings config = _settings.Current.Cloud;

        // Já enviada e ainda no ar: recopia o link em vez de subir a mesma imagem de
        // novo. Poupa a cota do serviço e evita dois links para a mesma captura.
        CloudLink? jaEnviada = _links.ParaArquivo(path);

        if (jaEnviada is not null)
        {
            toast.ShowCloudResult(
                CopiarTexto(jaEnviada.Url)
                    ? $"Link copiado de novo. {Restante(jaEnviada.ExpiresAt)}"
                    : "O link existe, mas não consegui copiá-lo: a área de transferência estava ocupada.",
                true,
                AvisoDeLeitura);
            return;
        }

        if (config.WarnBeforeSending && !toast.CloudConfirmationAsked)
        {
            toast.AskCloudConfirmation();
            return;
        }

        // O segundo clique de um clique duplo no botao chegaria aqui e confirmaria o envio
        // sem a pessoa ter lido a pergunta.
        if (config.WarnBeforeSending && !toast.CloudConfirmationReady)
        {
            return;
        }

        toast.ShowSending();

        UploadOutcome resultado = await _cloud.UploadAsync(path);

        if (!resultado.Success || resultado.Link is null)
        {
            _log.Warning("Envio para a nuvem falhou: {Motivo}", resultado.Message);
            toast.ShowCloudResult(resultado.Message, false, AvisoDeLeitura);
            return;
        }

        _links.Add(resultado.Link);

        // A pergunta já foi feita e respondida. Repeti-la a cada envio transformaria
        // um aviso útil em obstáculo -- que é como se ensina alguém a clicar sem ler.
        if (config.WarnBeforeSending)
        {
            _settings.Update(atual => atual with { Cloud = atual.Cloud with { WarnBeforeSending = false } });
        }

        string mensagem = $"Link criado. {Restante(resultado.Link.ExpiresAt)}";

        if (config.CopyLinkAfterSending)
        {
            mensagem = CopiarTexto(resultado.Link.Url)
                ? $"Link copiado. {Restante(resultado.Link.ExpiresAt)}"
                : "Link criado, mas não consegui copiá-lo: a área de transferência estava ocupada.";
        }

        toast.ShowCloudResult(mensagem, true, AvisoDeLeitura);
    }

    /// <summary>Quanto falta para o link morrer, em frase curta.</summary>
    private static string Restante(DateTimeOffset expira)
    {
        TimeSpan falta = expira - DateTimeOffset.Now;

        return falta <= TimeSpan.Zero
            ? "Já expirou."
            : $"Expira em {(int)falta.TotalHours}h.";
    }

    private bool CopiarTexto(string texto)
        => _clipboard.Write(
            _messageWindow.Handle,
            new ClipboardPayload(Image: null, Text: texto, FilePath: null));

    private void CopyPath(string path)
    {
        string text = PathTextFormatter.Format(path, _settings.Current.Clipboard);
        _clipboard.Write(_messageWindow.Handle, new ClipboardPayload(Image: null, Text: text, FilePath: path));
        _log.Debug("Caminho copiado a pedido do aviso");
    }

}
