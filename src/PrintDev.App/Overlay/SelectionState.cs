using System.ComponentModel;
using System.Runtime.CompilerServices;
using PrintDev.Core.Configuration;
using PrintDev.Core.Screens;

namespace PrintDev.Overlay;

/// <summary>
/// O estado do seletor, compartilhado por todas as janelas do overlay.
/// <para>
/// Existe <b>um só</b> destes, e todas as coordenadas aqui estão em <b>pixels físicos do
/// desktop virtual</b>. Cada janela desenha a interseção do retângulo com o próprio
/// monitor, então a moldura corta sozinha na borda da tela e simplesmente não é
/// desenhada nos buracos do desktop. Nenhuma janela é dona do retângulo — é isso que faz
/// arrastar de um monitor para o outro funcionar sem nenhum caso especial.
/// </para>
/// </summary>
public sealed class SelectionState : INotifyPropertyChanged
{
    private PixelRect _selection;
    private PixelRect _hoveredWindow;
    private (int X, int Y) _cursor;
    private bool _isDragging;
    private bool _hasSelection;
    private CaptureMode _mode = CaptureMode.Regiao;
    private int _magnifierZoom = 10;
    private bool _showMagnifier = true;
    private bool _showHints = true;
    private bool _pickingColor;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Disparado quando qualquer coisa que afeta o desenho muda.</summary>
    public event EventHandler? VisualChanged;

    /// <summary>Retângulo selecionado, em pixels físicos.</summary>
    public PixelRect Selection
    {
        get => _selection;
        set => Set(ref _selection, value);
    }

    /// <summary>Retângulo da janela sob o cursor, no modo Janela.</summary>
    public PixelRect HoveredWindow
    {
        get => _hoveredWindow;
        set => Set(ref _hoveredWindow, value);
    }

    /// <summary>Posição do cursor, em pixels físicos.</summary>
    public (int X, int Y) Cursor
    {
        get => _cursor;
        set => Set(ref _cursor, value);
    }

    /// <summary>Verdadeiro enquanto o botão do mouse está pressionado arrastando.</summary>
    public bool IsDragging
    {
        get => _isDragging;
        set => Set(ref _isDragging, value);
    }

    /// <summary>Verdadeiro quando já existe um retângulo escolhido.</summary>
    public bool HasSelection
    {
        get => _hasSelection;
        set => Set(ref _hasSelection, value);
    }

    /// <summary>Modo de seleção em vigor.</summary>
    public CaptureMode Mode
    {
        get => _mode;
        set => Set(ref _mode, value);
    }

    /// <summary>Ampliação da lupa.</summary>
    public int MagnifierZoom
    {
        get => _magnifierZoom;
        set => Set(ref _magnifierZoom, Math.Clamp(value, 2, 32));
    }

    /// <summary>Se a lupa aparece.</summary>
    public bool ShowMagnifier
    {
        get => _showMagnifier;
        set => Set(ref _showMagnifier, value);
    }

    /// <summary>
    /// Modo conta-gotas: sem véu e sem seleção, só a lupa. O véu falsearia justamente o
    /// que se está tentando medir — a cor real do pixel.
    /// </summary>
    public bool PickingColor
    {
        get => _pickingColor;
        set => Set(ref _pickingColor, value);
    }

    /// <summary>Se a faixa de dicas de teclado aparece.</summary>
    public bool ShowHints
    {
        get => _showHints;
        set => Set(ref _showHints, value);
    }

    /// <summary>
    /// O retângulo que seria capturado agora — o selecionado, ou o destacado pelo modo
    /// em vigor quando ainda não há seleção.
    /// </summary>
    public PixelRect Effective => HasSelection ? Selection : HoveredWindow;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

        // Um evento so para redesenhar: as janelas nao precisam saber QUAL propriedade
        // mudou, e assinar uma por uma seria ruido puro.
        VisualChanged?.Invoke(this, EventArgs.Empty);
    }
}
