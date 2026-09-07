using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;
using PrintDev.Core.Screens;
using Serilog;

namespace PrintDev.Overlay;

/// <summary>O que o usuário escolheu no seletor.</summary>
/// <param name="Region">Retângulo escolhido, em pixels físicos.</param>
/// <param name="Frozen">A tela congelada no instante em que o seletor abriu.</param>
/// <param name="Mode">Modo usado, para o nome do arquivo.</param>
public sealed record OverlayResult(PixelRect Region, CapturedImage Frozen, CaptureMode Mode);

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
    private CapturedImage? _frozen;
    private BitmapSource? _frozenBitmap;
    private (int X, int Y) _anchor;
    private bool _hintsShown;

    public OverlayCoordinator(ILogger log) => _log = log.ForContext<OverlayCoordinator>();

    /// <summary>Estado compartilhado por todas as janelas do seletor.</summary>
    internal SelectionState State { get; } = new();

    /// <summary>Verdadeiro enquanto o seletor está na tela.</summary>
    public bool IsOpen => _completion is not null;

    /// <summary>
    /// Mostra o seletor e espera a escolha. Devolve <see langword="null"/> se o usuário
    /// cancelar.
    /// </summary>
    public Task<OverlayResult?> ShowAsync(AppSettings settings)
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
        _frozen = GdiScreenCapture.CaptureAllMonitors(monitors);
        _frozenBitmap = _frozen.ToBitmapSource();

        // 2. Fotografar as janelas tambem antes: com o overlay na tela, perguntar quem
        //    esta sob o cursor devolveria sempre o proprio overlay.
        _targets = WindowEnumerator.Snapshot(_frozen.Bounds);

        ResetState(settings);

        _completion = new TaskCompletionSource<OverlayResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        MonitorInfo? active = VirtualDesktop.MonitorUnderCursor(monitors);

        foreach (MonitorInfo monitor in monitors)
        {
            var window = new OverlayWindow(monitor, _frozen, _frozenBitmap, this);
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

        _frozen = GdiScreenCapture.CaptureAllMonitors(monitors);
        _frozenBitmap = _frozen.ToBitmapSource();
        _targets = [];

        ResetState(settings);
        State.PickingColor = true;
        State.ShowMagnifier = true;
        State.ShowHints = false;

        _colorCompletion = new TaskCompletionSource<Color?>(TaskCreationOptions.RunContinuationsAsynchronously);

        MonitorInfo? active = VirtualDesktop.MonitorUnderCursor(monitors);

        foreach (MonitorInfo monitor in monitors)
        {
            var window = new OverlayWindow(monitor, _frozen, _frozenBitmap, this);
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

    private Color ReadColor(int x, int y)
    {
        if (_frozen is null || !_frozen.Bounds.Contains(x, y))
        {
            return Colors.Black;
        }

        int offset = ((y - _frozen.Bounds.Top) * _frozen.Stride)
                     + ((x - _frozen.Bounds.Left) * CapturedImage.BytesPerPixel);

        // Os bytes estao em BGRA.
        return Color.FromRgb(_frozen.Pixels[offset + 2], _frozen.Pixels[offset + 1], _frozen.Pixels[offset]);
    }

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

        // As dicas aparecem uma vez por sessao: na segunda captura elas ja seriam ruido.
        State.ShowHints = !_hintsShown;
        _hintsShown = true;

        UpdateHover();
    }

    /// <summary>O ponteiro se moveu. A posição vem sempre do Windows, nunca do evento.</summary>
    internal void PointerMoved()
    {
        (int x, int y) = VirtualDesktop.CursorPosition();
        State.Cursor = (x, y);

        if (State.IsDragging)
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
        _anchor = VirtualDesktop.CursorPosition();
        State.Cursor = _anchor;
        State.IsDragging = true;
        State.ShowHints = false;
        _log.Debug("Arrasto iniciado em {X},{Y}", _anchor.X, _anchor.Y);
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

        State.IsDragging = false;
        (int x, int y) = VirtualDesktop.CursorPosition();

        const int clickThreshold = 5;
        bool wasClick = Math.Abs(x - _anchor.X) < clickThreshold && Math.Abs(y - _anchor.Y) < clickThreshold;
        _log.Debug("Arrasto terminou em {X},{Y}; selecao {Selecao}", x, y, State.Selection);

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

    /// <summary>
    /// Fecha o seletor devolvendo o recorte. Um retângulo vazio conta como cancelamento.
    /// </summary>
    internal void Confirm(PixelRect region)
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
        CapturedImage frozen = _frozen;
        Close();
        _completion?.TrySetResult(new OverlayResult(clipped, frozen, mode));
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
        _frozenBitmap = null;

        // _frozen NAO e limpo aqui: ele acabou de ser entregue a quem chamou, que ainda
        // precisa recortar a imagem a partir dele.
    }
}
