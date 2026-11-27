using System.IO;
using PrintDev.Core.Clipboard;
using PrintDev.Core.Hotkeys;
using PrintDev.Core.Storage;
using Serilog;

namespace PrintDev.Core.History;

/// <summary>O que aconteceu ao tentar desfazer uma captura.</summary>
/// <param name="Success">Se o arquivo saiu do lugar.</param>
/// <param name="Message">Frase pronta para mostrar ao usuário, em português.</param>
/// <param name="ClipboardCleared">
/// Se a área de transferência também foi limpa. Falso quando o conteúdo já era de outro
/// programa — o que é um desfecho correto, não um erro.
/// </param>
public sealed record UndoOutcome(bool Success, string Message, bool ClipboardCleared);

/// <summary>
/// Desfaz uma captura: manda o arquivo para a Lixeira, tira do histórico e devolve a
/// área de transferência ao que era antes.
/// <para>
/// É fonte única de propósito. Desfazer é acionável de três lugares — o botão do aviso,
/// o atalho global e o menu da bandeja — e cada cópia da regra seria uma chance de um
/// deles esquecer a área de transferência ou o histórico.
/// </para>
/// <para>
/// Vai para a Lixeira, e não para o nada, porque desfazer é reversível por definição:
/// quem clicou errado precisa poder voltar atrás do voltar atrás.
/// </para>
/// </summary>
public sealed class CaptureUndoService
{
    private readonly CaptureHistory _history;
    private readonly ClipboardWriter _clipboard;
    private readonly HotkeyMessageWindow _messageWindow;
    private readonly ILogger _log;

    public CaptureUndoService(
        CaptureHistory history,
        ClipboardWriter clipboard,
        HotkeyMessageWindow messageWindow,
        ILogger log)
    {
        _history = history;
        _clipboard = clipboard;
        _messageWindow = messageWindow;
        _log = log.ForContext<CaptureUndoService>();
    }

    /// <summary>
    /// Uma captura foi desfeita, venha o pedido de onde vier (aviso, atalho, bandeja).
    /// <para>
    /// Quem guarda referência à captura precisa saber: o aviso dela continuaria oferecendo
    /// "Copiar caminho" de um arquivo na Lixeira, e o atalho de colar como caminho
    /// colaria um caminho morto no terminal.
    /// </para>
    /// </summary>
    public event EventHandler<CaptureHistoryItem>? Undone;

    /// <summary>Desfaz a captura mais recente, se houver alguma.</summary>
    public UndoOutcome UndoLast()
    {
        CaptureHistoryItem? item = _history.Latest;

        return item is null
            ? new UndoOutcome(false, "Não há captura recente para desfazer.", false)
            : Undo(item);
    }

    /// <summary>Desfaz a captura indicada.</summary>
    public UndoOutcome Undo(CaptureHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Captura que nunca chegou ao disco — a gravação falhou e ela foi só para a área
        // de transferência. Não há arquivo a recolher, mas há o que limpar.
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            _history.Remove(item);
            bool limpou = _clipboard.ClearIfOwned(_messageWindow.Handle, capturePath: null);
            _log.Information("Captura sem arquivo removida do histórico.");
            Undone?.Invoke(this, item);
            return new UndoOutcome(true, "Captura descartada.", limpou);
        }

        // O arquivo pode ter sido movido ou renomeado depois da captura. A Lixeira
        // responderia "feito" sem ter feito nada; dizer que o arquivo foi para la seria
        // mentira, e o arquivo continua no disco.
        bool existia = File.Exists(item.Path);

        if (existia && !RecycleBin.Send(item.Path, out string? motivo, perguntarAntesDeApagarDeVez: true))
        {
            _log.Warning(
                "Não consegui mandar {Arquivo} para a Lixeira: {Motivo}",
                item.Path,
                motivo ?? "motivo desconhecido");

            return new UndoOutcome(false, $"Não consegui desfazer: {motivo}.", false);
        }

        // A ordem importa: o arquivo primeiro. Tirar do histórico antes e falhar na
        // Lixeira deixaria um arquivo em disco que o programa não sabe mais que existe.
        _history.Remove(item);

        bool clipboardLimpo = _clipboard.ClearIfOwned(_messageWindow.Handle, item.Path);
        Undone?.Invoke(this, item);

        if (!existia)
        {
            _log.Information(
                "Captura desfeita sem arquivo: {Arquivo} já não estava lá (área de transferência limpa: {Limpou})",
                item.Path,
                clipboardLimpo);

            return new UndoOutcome(true, "O arquivo já não estava na pasta; a captura saiu do histórico.", clipboardLimpo);
        }

        _log.Information(
            "Captura desfeita: {Arquivo} foi para a Lixeira (área de transferência limpa: {Limpou})",
            item.Path,
            clipboardLimpo);

        return new UndoOutcome(true, "Captura desfeita: o arquivo foi para a Lixeira.", clipboardLimpo);
    }
}
