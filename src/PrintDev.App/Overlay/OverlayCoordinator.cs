using System.Windows.Media;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.Runtime;
using PrintDev.Core.Screens;
using Serilog;

namespace PrintDev.Overlay;

/// <summary>O que o usuário escolheu no seletor.</summary>
/// <param name="Region">Retângulo escolhido, em pixels físicos.</param>
/// <param name="Frozen">A tela congelada no instante em que o seletor abriu.</param>
/// <param name="Mode">Modo usado, para o nome do arquivo.</param>
/// <param name="Action">O que fazer com o recorte. Só muda pela barra do modo de ajuste.</param>
public sealed record OverlayResult(
    PixelRect Region,
    FrozenScreen Frozen,
    CaptureMode Mode,
    OverlayAction Action = OverlayAction.Capturar);

/// <summary>O destino escolhido na barra de ações do modo de ajuste.</summary>
public enum OverlayAction
{
    /// <summary>O caminho normal: grava, copia e avisa.</summary>
    Capturar,

    /// <summary>Abre o editor de anotação com o recorte.</summary>
    Anotar,

    /// <summary>Fixa o recorte na tela, sempre por cima, sem gravar.</summary>
    Fixar,

    /// <summary>Reconhece o texto do recorte e copia.</summary>
    Texto,

    /// <summary>Grava, copia e envia para a nuvem.</summary>
    Nuvem,
}

/// <summary>
/// Abre o seletor de área e devolve o recorte escolhido.
/// <para>
/// Cria <b>uma janela por monitor</b>, e não uma janela gigante cobrindo tudo. Uma
/// janela só teria dois problemas insolúveis: seria escalada pelo fator de um único
/// monitor, deformando lupa, badge e barra no outro; e pintaria por cima dos buracos do
/// desktop virtual, que nesta máquina existem de verdade porque as duas telas têm alturas
/// diferentes.
/// </para>
/// </summary>
public sealed class OverlayCoordinator
{
    private readonly ILogger _log;

    private TaskCompletionSource<OverlayResult?>? _completion;
    private TaskCompletionSource<Color?>? _colorCompletion;
    private readonly List<OverlayWindow> _windows = [];
    private IReadOnlyList<WindowTarget> _targets = [];
    private FrozenScreen? _frozen;
    private (int X, int Y) _anchor;
    private bool _hintsShown;

    /// <summary>A alça pega no começo do arrasto, no modo de ajuste.</summary>
    private SelectionGrip _grip;

    /// <summary>A seleção como estava quando o arrasto começou.</summary>
    private PixelRect _gripStart;

    public OverlayCoordinator(ILogger log)
    {
        _log = log.ForContext<OverlayCoordinator>();

        // Os dois vivem o programa inteiro: esta inscricao nao prende janela nenhuma.
        // E ela que reposiciona a barra de acoes a cada mudanca da selecao.
        State.VisualChanged += (_, _) =>
        {
            foreach (OverlayWindow window in _windows)
            {
                window.SyncActionBar();
            }
        };
    }

    /// <summary>Estado compartilhado por todas as janelas do seletor.</summary>
    internal SelectionState State { get; } = new();

    /// <summary>Verdadeiro enquanto o seletor está na tela.</summary>
    public bool IsOpen => _completion is not null;

    /// <summary>
    /// Mostra o seletor e espera a escolha. Devolve <see langword="null"/> se o usuário
    /// cancelar.
    /// </summary>
    /// <param name="settings">As configurações em vigor.</param>
    /// <param name="allowActions">
    /// Se a barra de ações do modo de ajuste aparece. Falso quando quem abriu já sabe o
    /// destino, como o reconhecimento de texto.
    /// </param>
    public Task<OverlayResult?> ShowAsync(AppSettings settings, bool allowActions = true)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_completion is not null)
        {
            // Apertar a tecla de novo com o seletor aberto nao pode abrir um segundo.
            _log.Debug("O seletor já está aberto; ignorando.");
            return _completion.Task;
        }

        IReadOnlyList<MonitorInfo> monitors = VirtualDesktop.Enumerate();
        if (monitors.Count == 0)
        {
            _log.Warning("Nenhum monitor encontrado.");
            return Task.FromResult<OverlayResult?>(null);
        }

        // 1. Congelar a tela ANTES de qualquer janela nossa aparecer. Sem isso o seletor
        //    se captura, e o que o usuario recorta nao e o que ele estava vendo.
        _frozen = GdiScreenCapture.FreezeAllMonitors(monitors);

        // 2. Fotografar as janelas tambem antes: com o overlay na tela, perguntar quem
        //    esta sob o cursor devolveria sempre o proprio overlay.
        _targets = WindowEnumerator.Snapshot(_frozen.Bounds);

        ResetState(settings);
        State.ActionsAllowed = allowActions;

        _completion = new TaskCompletionSource<OverlayResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        MonitorInfo? active = VirtualDesktop.MonitorUnderCursor(monitors);

        foreach (MonitorInfo monitor in monitors)
        {
            var window = new OverlayWindow(monitor, _frozen, this);
            _windows.Add(window);
            window.Show();
        }

        // Só a janela do monitor sob o cursor recebe foco: é ela que vai processar o
        // teclado, e ativar todas faria as janelas brigarem entre si pelo primeiro plano.
        _windows.FirstOrDefault(w => w.Monitor.Handle == active?.Handle)?.TakeFocus();

        _log.Debug("Seletor aberto em {Quantidade} monitor(es)", _windows.Count);
        return _completion.Task;
    }

    /// <summary>
    /// Abre o conta-gotas e devolve a cor escolhida.
    /// <para>
    /// Reaproveita as mesmas janelas do seletor: a tela já é congelada, a lupa já lê o
    /// pixel e a coordenada já está certa em qualquer monitor. Um overlay separado só
    /// para isso seria uma segunda implementação das mesmas contas.
    /// </para>
    /// </summary>
    public Task<Color?> PickColorAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_completion is not null || _colorCompletion is not null)
        {
            return Task.FromResult<Color?>(null);
        }

        IReadOnlyList<MonitorInfo> monitors = VirtualDesktop.Enumerate();
        if (monitors.Count == 0)
        {
            return Task.FromResult<Color?>(null);
        }

        _frozen = GdiScreenCapture.FreezeAllMonitors(monitors);
        _targets = [];

        ResetState(settings);
        State.PickingColor = true;
        State.ShowMagnifier = true;
        State.ShowHints = false;

        _colorCompletion = new TaskCompletionSource<Color?>(TaskCreationOptions.RunContinuationsAsynchronously);

        MonitorInfo? active = VirtualDesktop.MonitorUnderCursor(monitors);

        foreach (MonitorInfo monitor in monitors)
        {
            var window = new OverlayWindow(monitor, _frozen, this);
            _windows.Add(window);
            window.Show();
        }

        _windows.FirstOrDefault(w => w.Monitor.Handle == active?.Handle)?.TakeFocus();
        _log.Debug("Conta-gotas aberto");

        return _colorCompletion.Task;
    }

    /// <summary>Lê a cor sob o cursor e fecha o conta-gotas.</summary>
    internal void ConfirmColor()
    {
        if (_colorCompletion is null || _frozen is null)
        {
            return;
        }

        (int x, int y) = VirtualDesktop.CursorPosition();
        Color color = ReadColor(x, y);

        Close();
        _colorCompletion.TrySetResult(color);
        _colorCompletion = null;
        State.PickingColor = false;
    }

    private Color ReadColor(int x, int y) => _frozen?.ReadPixel(x, y) ?? Colors.Black;

    private void ResetState(AppSettings settings)
    {
        State.Selection = PixelRect.Empty;
        State.HoveredWindow = PixelRect.Empty;
        State.HasSelection = false;
        State.IsDragging = false;
        State.Mode = settings.Capture.DefaultMode;
        State.ShowMagnifier = settings.Capture.ShowMagnifier;
        State.MagnifierZoom = settings.Capture.MagnifierZoom;
        State.Cursor = VirtualDesktop.CursorPosition();
        State.PickingColor = false;
        State.Adjusting = false;
        State.ActionsAllowed = false;
        _grip = SelectionGrip.None;

        // As dicas aparecem uma vez por sessao: na segunda captura elas ja seriam ruido.
        State.ShowHints = !_hintsShown;
        _hintsShown = true;

        UpdateHover();
    }

    /// <summary>O ponteiro se moveu. A posição vem sempre do Windows, nunca do evento.</summary>
    internal void PointerMoved() => PointerAt(VirtualDesktop.CursorPosition());

    /// <summary>Atualiza a seleção e o destaque para o cursor neste ponto.</summary>
    private void PointerAt((int X, int Y) position)
    {
        (int x, int y) = position;
        State.Cursor = (x, y);

        if (State.IsDragging && _grip != SelectionGrip.None)
        {
            // Modo de ajuste: a alca pega no comeco decide o que o arrasto muda.
            State.Selection = SelectionGrips.Drag(
                _gripStart, _grip, x - _anchor.X, y - _anchor.Y, _frozen?.Bounds ?? PixelRect.Empty);
        }
        else if (State.IsDragging)
        {
            State.Selection = PixelRect
                .FromPoints(_anchor.X, _anchor.Y, x, y)
                .Intersect(_frozen?.Bounds ?? PixelRect.Empty);
            State.HasSelection = true;
        }
        else
        {
            UpdateHover();
        }
    }

    /// <summary>Começou o arrasto.</summary>
    internal void DragStarted()
    {
        // Onde o botao foi apertado, e nao onde o cursor esta agora: veja MessagePosition.
        _anchor = VirtualDesktop.MessagePosition();
        State.Cursor = _anchor;
        _gripStart = State.Selection;
        _grip = State.Adjusting && State.HasSelection
            ? SelectionGrips.HitTest(State.Selection, _anchor.X, _anchor.Y, GripTolerance(_anchor.X, _anchor.Y))
            : SelectionGrip.None;
        State.IsDragging = true;
        State.ShowHints = false;
        _log.Debug(
            "Arrasto iniciado em {X},{Y}; ajuste={Ajuste} alca={Alca} selecao={Selecao}",
            _anchor.X, _anchor.Y, State.Adjusting, _grip, State.Selection);
    }

    /// <summary>
    /// Terminou o arrasto. Um arrasto curto demais é tratado como clique — é o gesto de
    /// quem quer a janela inteira e não uma região.
    /// </summary>
    internal void DragEnded()
    {
        if (!State.IsDragging)
        {
            return;
        }

        // A selecao termina onde o botao foi solto. A ultima posicao antes de soltar pode
        // nao ter chegado como movimento, e o cursor ja pode ter andado depois dela.
        (int x, int y) = VirtualDesktop.MessagePosition();
        PointerAt((x, y));
        State.IsDragging = false;

        const int clickThreshold = 5;
        bool wasClick = Math.Abs(x - _anchor.X) < clickThreshold && Math.Abs(y - _anchor.Y) < clickThreshold;
        _log.Debug("Arrasto terminou em {X},{Y}; selecao {Selecao}", x, y, State.Selection);

        if (_grip != SelectionGrip.None)
        {
            // Terminou de mover ou redimensionar: continua no modo de ajuste.
            _grip = SelectionGrip.None;
            return;
        }

        if (State.Adjusting)
        {
            // No modo de ajuste, arrastar fora faz outra selecao, e clicar fora nao
            // desfaz a que existia. Nada e capturado sem confirmar.
            if (wasClick)
            {
                State.Selection = _gripStart;
                State.HasSelection = !_gripStart.IsEmpty;
            }

            return;
        }

        if (!wasClick && !State.Selection.IsEmpty
            && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            // Shift ao soltar: a selecao fica na tela, com alcas, ate confirmar. Sem o
            // Shift continua como sempre foi - soltar captura na hora.
            State.Adjusting = true;
            State.ShowHints = false;
            _log.Debug("Modo de ajuste em {Selecao}", State.Selection);
            return;
        }

        if (wasClick)
        {
            State.HasSelection = false;
            UpdateHover();
            Confirm(State.HoveredWindow);
            return;
        }

        Confirm(State.Selection);
    }

    /// <summary>Troca o modo de seleção.</summary>
    internal void SetMode(CaptureMode mode)
    {
        State.Adjusting = false;
        State.Mode = mode;
        State.HasSelection = false;
        State.Selection = PixelRect.Empty;
        UpdateHover();
    }

    /// <summary>Avança para o próximo modo, na ordem da barra.</summary>
    internal void CycleMode()
    {
        CaptureMode next = State.Mode switch
        {
            CaptureMode.Regiao => CaptureMode.Janela,
            CaptureMode.Janela => CaptureMode.Monitor,
            CaptureMode.Monitor => CaptureMode.TelaInteira,
            _ => CaptureMode.Regiao,
        };

        SetMode(next);
        foreach (OverlayWindow window in _windows)
        {
            window.SyncMode(next);
        }
    }

    /// <summary>Confirma a seleção que estiver em vigor.</summary>
    internal void ConfirmCurrent() => Confirm(State.Effective);

    /// <summary>Confirma a seleção com o destino escolhido na barra de ações.</summary>
    internal void ConfirmWith(OverlayAction action) => Confirm(State.Effective, action);

    /// <summary>
    /// Duplo clique dentro da seleção, no modo de ajuste, confirma. Devolve se confirmou.
    /// </summary>
    internal bool ConfirmByDoubleClick()
    {
        (int x, int y) = VirtualDesktop.MessagePosition();

        if (!State.Adjusting || !State.HasSelection || !State.Selection.Contains(x, y))
        {
            return false;
        }

        ConfirmCurrent();
        return true;
    }

    /// <summary>
    /// A alça sob o cursor, para a janela escolher o ponteiro. Durante um arrasto vale a
    /// que foi pega no começo, mesmo que o cursor já tenha saído de cima dela.
    /// </summary>
    internal SelectionGrip GripUnderCursor()
    {
        if (State.IsDragging)
        {
            return _grip;
        }

        if (!State.Adjusting || !State.HasSelection)
        {
            return SelectionGrip.None;
        }

        (int x, int y) = State.Cursor;
        return SelectionGrips.HitTest(State.Selection, x, y, GripTolerance(x, y));
    }

    /// <summary>
    /// O monitor onde a barra de ações aparece: o do canto inferior direito da seleção,
    /// que é onde a mão está ao terminar o arrasto. Se esse canto cair no buraco entre
    /// dois monitores, vale outro canto, e por fim o monitor do cursor.
    /// </summary>
    internal IntPtr ActionBarMonitor()
    {
        PixelRect selection = State.Selection;
        (int X, int Y)[] corners =
        [
            (selection.Right - 1, selection.Bottom - 1),
            (selection.Left, selection.Bottom - 1),
            (selection.Right - 1, selection.Top),
            (selection.Left, selection.Top),
            State.Cursor,
        ];

        foreach ((int x, int y) in corners)
        {
            MonitorInfo? monitor = _windows.Select(w => w.Monitor).FirstOrDefault(m => m.Bounds.Contains(x, y));
            if (monitor is not null)
            {
                return monitor.Handle;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Folga para pegar uma alça, em pixels físicos: seis unidades de tela no monitor do
    /// ponto, para a alça ser igualmente fácil de pegar em qualquer escala.
    /// </summary>
    private int GripTolerance(int x, int y)
    {
        double scale = _windows.Select(w => w.Monitor).FirstOrDefault(m => m.Bounds.Contains(x, y))?.ScaleX ?? 1;
        return (int)Math.Round(6 * scale);
    }

    /// <summary>
    /// Fecha o seletor devolvendo o recorte. Um retângulo vazio conta como cancelamento.
    /// </summary>
    internal void Confirm(PixelRect region, OverlayAction action = OverlayAction.Capturar)
    {
        if (_completion is null || _frozen is null)
        {
            return;
        }

        PixelRect clipped = region.Intersect(_frozen.Bounds);

        if (clipped.IsEmpty)
        {
            Cancel();
            return;
        }

        CaptureMode mode = State.Mode;
        FrozenScreen frozen = _frozen;
        Close();
        _completion?.TrySetResult(new OverlayResult(clipped, frozen, mode, action));
        _completion = null;
    }

    /// <summary>Fecha o seletor sem capturar nada.</summary>
    internal void Cancel()
    {
        if (_colorCompletion is not null)
        {
            Close();
            _colorCompletion.TrySetResult(null);
            _colorCompletion = null;
            State.PickingColor = false;
            return;
        }

        if (_completion is null)
        {
            return;
        }

        Close();
        _completion.TrySetResult(null);
        _completion = null;
        _log.Debug("Seletor cancelado");
    }

    /// <summary>
    /// Esc com uma seleção feita apenas a limpa; sem seleção, fecha. Assim o usuário
    /// pode refazer o recorte sem precisar reabrir o seletor.
    /// </summary>
    internal void EscapePressed()
    {
        if (State.HasSelection)
        {
            State.Adjusting = false;
            State.HasSelection = false;
            State.Selection = PixelRect.Empty;
            UpdateHover();
            return;
        }

        Cancel();
    }

    /// <summary>Move a seleção sem mudar o tamanho.</summary>
    internal void NudgeSelection(int dx, int dy)
    {
        if (!State.HasSelection || _frozen is null)
        {
            return;
        }

        State.Selection = State.Selection.Offset(dx, dy).ClampInside(_frozen.Bounds);
    }

    /// <summary>Redimensiona pelo canto inferior direito.</summary>
    internal void ResizeSelection(int dw, int dh)
    {
        if (!State.HasSelection || _frozen is null)
        {
            return;
        }

        PixelRect resized = State.Selection with
        {
            Width = Math.Max(1, State.Selection.Width + dw),
            Height = Math.Max(1, State.Selection.Height + dh),
        };

        State.Selection = resized.ClampInside(_frozen.Bounds);
    }

    /// <summary>Ajusta a ampliação da lupa.</summary>
    internal void ZoomMagnifier(int delta) => State.MagnifierZoom += delta;

    /// <summary>
    /// Recalcula o que fica destacado quando não há arrasto, conforme o modo.
    /// </summary>
    private void UpdateHover()
    {
        if (_frozen is null)
        {
            return;
        }

        (int x, int y) = State.Cursor;

        State.HoveredWindow = State.Mode switch
        {
            CaptureMode.TelaInteira => _frozen.Bounds,
            CaptureMode.Monitor => MonitorRectAt(x, y),
            CaptureMode.Janela => WindowEnumerator.HitTest(_targets, x, y)?.Bounds ?? MonitorRectAt(x, y),

            // No modo Regiao nada fica destacado antes do arrasto: um retangulo pulando
            // de janela em janela enquanto a pessoa mira e distracao pura. O clique
            // simples ainda captura a janela sob o cursor.
            _ => PixelRect.Empty,
        };
    }

    private PixelRect MonitorRectAt(int x, int y)
    {
        MonitorInfo? monitor = _windows
            .Select(w => w.Monitor)
            .FirstOrDefault(m => m.Bounds.Contains(x, y));

        return monitor?.Bounds ?? _frozen?.Bounds ?? PixelRect.Empty;
    }

    private void Close()
    {
        foreach (OverlayWindow window in _windows)
        {
            window.CloseOverlay();
        }

        _windows.Clear();
        _targets = [];

        // Quem chamou ja recebeu a sua referencia no resultado e recorta a partir dela.
        // Guardar outra aqui segurava a tela inteira congelada ate a proxima captura,
        // que pode ser daqui a horas.
        _frozen = null;

        // Cancelar nao mostra aviso nenhum, entao e aqui que a tela congelada volta ao
        // sistema. Quando houve captura, o aviso que vem em seguida adia este pedido.
        WorkingSet.ReleaseSoon();
    }
}
