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
    public void Show(CaptureHistoryItem item, PixelRect near)
    {
        ArgumentNullException.ThrowIfNull(item);

        CaptureSettings capture = _settings.Current.Capture;
        if (!capture.ShowToast)
        {
            return;
        }

        while (_visible.Count >= MaxVisible)
        {
            _visible[0].Dismiss();
            _visible.RemoveAt(0);
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
                WorkingSet.Trim();
            }
        };

        _visible.Add(toast);
        toast.ShowAt(SlotFor(near, _visible.Count - 1, toast.MeasuredHeight));
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

        Show(item, near);

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

        CloudSettings config = _settings.Current.Cloud;

        // Já enviada e ainda no ar: recopia o link em vez de subir a mesma imagem de
        // novo. Poupa a cota do serviço e evita dois links para a mesma captura.
        CloudLink? jaEnviada = _links.ParaArquivo(item.Path);

        if (jaEnviada is not null)
        {
            CopiarTexto(jaEnviada.Url);
            toast.ShowCloudResult($"Link copiado de novo. {Restante(jaEnviada.ExpiresAt)}", true, AvisoDeLeitura);
            return;
        }

        if (config.WarnBeforeSending && !toast.CloudConfirmationAsked)
        {
            toast.AskCloudConfirmation();
            return;
        }

        toast.ShowSending();

        UploadOutcome resultado = await _cloud.UploadAsync(item.Path);

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

        if (config.CopyLinkAfterSending)
        {
            CopiarTexto(resultado.Link.Url);
        }

        toast.ShowCloudResult($"Link copiado. {Restante(resultado.Link.ExpiresAt)}", true, AvisoDeLeitura);
    }

    /// <summary>Quanto falta para o link morrer, em frase curta.</summary>
    private static string Restante(DateTimeOffset expira)
    {
        TimeSpan falta = expira - DateTimeOffset.Now;

        return falta <= TimeSpan.Zero
            ? "Já expirou."
            : $"Expira em {(int)falta.TotalHours}h.";
    }

    private void CopiarTexto(string texto)
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
