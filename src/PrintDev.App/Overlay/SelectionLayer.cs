using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PrintDev.Core.Capture;
using PrintDev.Core.Screens;

namespace PrintDev.Overlay;

/// <summary>
/// Desenha tudo que o seletor mostra por cima da tela congelada: o véu, a moldura da
/// seleção, as alças, o badge de dimensões, a mira e a lupa.
/// <para>
/// É um elemento com desenho próprio, e não uma árvore de formas do WPF, porque tudo
/// aqui é redesenhado a cada movimento do mouse sobre uma área que pode passar de três
/// milhões de pixels. Uma passada de <see cref="DrawingContext"/> é ordens de grandeza
/// mais barata do que atualizar dezenas de objetos com vínculo de dados.
/// </para>
/// </summary>
public sealed class SelectionLayer : FrameworkElement
{
    /// <summary>Tamanho da lupa, em unidades independentes de dispositivo.</summary>
    private const double MagnifierSize = 148;

    /// <summary>
    /// Altura do rodape da lupa. Duas linhas: o HEX com a amostra de cor em cima, e as
    /// coordenadas embaixo.
    /// <para>
    /// Comecou como uma linha so, com o HEX a esquerda e as coordenadas a direita, e os
    /// dois se sobrepunham em qualquer valor de seis digitos. Empilhar e mais honesto do
    /// que ficar apertando fonte ate caber.
    /// </para>
    /// </summary>
    private const double MagnifierFooter = 44;

    /// <summary>Distância entre o cursor e a lupa.</summary>
    private const double MagnifierGap = 24;

    /// <summary>Grade de pixels só aparece a partir desta ampliação.</summary>
    private const int GridFromZoom = 6;

    /// <summary>Lado das alças quadradas.</summary>
    private const double HandleSize = 8;

    /// <summary>Braço dos colchetes de canto — o elemento-assinatura do produto.</summary>
    private const double BracketArm = 12;

    private readonly MonitorInfo _monitor;
    private readonly CapturedImage _frozen;
    private readonly BitmapSource _frozenBitmap;
    private readonly SelectionState _state;

    private readonly Brush _veil;
    private readonly Brush _sandwichOuter;
    private readonly Brush _sandwichInner;
    private readonly Brush _accent;
    private readonly Brush _badgeBackground;
    private readonly Brush _badgeText;
    private readonly Pen _outerPen;
    private readonly Pen _accentPen;
    private readonly Pen _innerPen;
    private readonly Pen _handlePen;
    private readonly Pen _crosshairPen;
    private readonly Typeface _mono;

    public SelectionLayer(
        MonitorInfo monitor,
        CapturedImage frozen,
        BitmapSource frozenBitmap,
        SelectionState state)
    {
        _monitor = monitor;
        _frozen = frozen;
        _frozenBitmap = frozenBitmap;
        _state = state;

        IsHitTestVisible = false;

        // A lupa precisa mostrar o pixel como ele e, quadrado e sem suavizacao - o
        // objetivo dela e justamente inspecionar pixel a pixel.
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        _veil = Resolve("Brush.Veil", Color.FromArgb(0x8C, 0, 0, 0));
        _sandwichOuter = Resolve("Brush.Sandwich.Outer", Color.FromArgb(0x8C, 0x0A, 0x0A, 0x0C));
        _sandwichInner = Resolve("Brush.Sandwich.Inner", Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        _accent = Resolve("Brush.Accent", Color.FromRgb(0xEC, 0x00, 0x8C));
        _badgeBackground = new SolidColorBrush(Color.FromArgb(0xD1, 0x0A, 0x0A, 0x0C));
        _badgeText = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF7));
        _badgeBackground.Freeze();
        _badgeText.Freeze();

        _outerPen = FrozenPen(_sandwichOuter, 1);
        _accentPen = FrozenPen(_accent, 1);
        _innerPen = FrozenPen(_sandwichInner, 1);
        _handlePen = FrozenPen(new SolidColorBrush(Color.FromArgb(0x99, 0x0A, 0x0A, 0x0C)), 1);
        _crosshairPen = FrozenPen(new SolidColorBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)), 1);

        _mono = new Typeface(
            (FontFamily)(Application.Current?.TryFindResource("Font.Mono") ?? new FontFamily("Consolas")),
            FontStyles.Normal,
            FontWeights.Medium,
            FontStretches.Normal);

        _state.VisualChanged += (_, _) => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    /// <inheritdoc/>
    protected override void OnRender(DrawingContext context)
    {
        double width = ActualWidth;
        double height = ActualHeight;

        if (width <= 0 || height <= 0)
        {
            return;
        }

        var full = new Rect(0, 0, width, height);

        if (_state.PickingColor)
        {
            // Sem veu: escurecer a tela mudaria a cor que o usuario esta medindo.
            if (CursorIsHere())
            {
                DrawCrosshair(context, full);
                DrawMagnifier(context, full);
            }

            return;
        }

        PixelRect effective = _state.Effective.Intersect(_monitor.Bounds);
        Rect hole = ToLocal(effective);

        DrawVeil(context, full, effective, hole);

        if (!effective.IsEmpty)
        {
            DrawSelectionFrame(context, hole);
            DrawDimensionBadge(context, effective, hole, full);

            if (!_state.IsDragging && _state.HasSelection)
            {
                DrawHandles(context, hole);
            }
        }
        else if (CursorIsHere())
        {
            DrawCrosshair(context, full);
        }

        if (_state.ShowMagnifier && CursorIsHere() && !_state.HasSelection)
        {
            DrawMagnifier(context, full);
        }
    }

    /// <summary>
    /// Escurece a tela e abre um furo na área selecionada.
    /// <para>
    /// O furo é feito com <b>uma única forma</b> de regra par-ímpar, e não com quatro
    /// retângulos em volta. Quatro retângulos deixam costura visível entre eles em
    /// frações de pixel, e custam quatro vezes mais a cada quadro do arrasto.
    /// </para>
    /// </summary>
    private void DrawVeil(DrawingContext context, Rect full, PixelRect effective, Rect hole)
    {
        if (effective.IsEmpty)
        {
            context.DrawRectangle(_veil, pen: null, full);
            return;
        }

        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };

        using (StreamGeometryContext figure = geometry.Open())
        {
            AddRectangle(figure, full);
            AddRectangle(figure, hole);
        }

        geometry.Freeze();
        context.DrawGeometry(_veil, pen: null, geometry);
    }

    private static void AddRectangle(StreamGeometryContext figure, Rect rect)
    {
        figure.BeginFigure(rect.TopLeft, isFilled: true, isClosed: true);
        figure.LineTo(rect.TopRight, isStroked: false, isSmoothJoin: false);
        figure.LineTo(rect.BottomRight, isStroked: false, isSmoothJoin: false);
        figure.LineTo(rect.BottomLeft, isStroked: false, isSmoothJoin: false);
    }

    /// <summary>
    /// Desenha a moldura da seleção como um sanduíche de três linhas de um pixel:
    /// escura por fora, magenta no meio, clara por dentro.
    /// <para>
    /// A cor sozinha nunca garante contraste contra um fundo arbitrário — e o fundo aqui
    /// é literalmente qualquer coisa que estivesse na tela. A sequência escuro, cor,
    /// claro sempre deixa pelo menos uma das três visível, seja qual for o pixel embaixo.
    /// O magenta então não precisa vencer o fundo: ele só precisa dizer de quem é a
    /// seleção.
    /// </para>
    /// </summary>
    private void DrawSelectionFrame(DrawingContext context, Rect hole)
    {
        context.DrawRectangle(null, _outerPen, Inflate(hole, 1.5));
        context.DrawRectangle(null, _accentPen, Inflate(hole, 0.5));
        context.DrawRectangle(null, _innerPen, Inflate(hole, -0.5));
    }

    /// <summary>
    /// Alças: colchetes de corte nos quatro cantos, quadrados nos meios das arestas.
    /// Os colchetes são a assinatura do produto e aparecem só onde significam alguma
    /// coisa — aqui, "esta área é recortável".
    /// </summary>
    private void DrawHandles(DrawingContext context, Rect hole)
    {
        if (hole.Width < 24 || hole.Height < 24)
        {
            return;
        }

        double arm = Math.Min(BracketArm, Math.Min(hole.Width, hole.Height) / 3);

        DrawBracket(context, hole.Left, hole.Top, arm, arm);
        DrawBracket(context, hole.Right, hole.Top, -arm, arm);
        DrawBracket(context, hole.Right, hole.Bottom, -arm, -arm);
        DrawBracket(context, hole.Left, hole.Bottom, arm, -arm);

        DrawSquareHandle(context, hole.Left + (hole.Width / 2), hole.Top);
        DrawSquareHandle(context, hole.Left + (hole.Width / 2), hole.Bottom);
        DrawSquareHandle(context, hole.Left, hole.Top + (hole.Height / 2));
        DrawSquareHandle(context, hole.Right, hole.Top + (hole.Height / 2));
    }

    private void DrawBracket(DrawingContext context, double x, double y, double armX, double armY)
    {
        var pen = new Pen(_sandwichInner, 3) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        pen.Freeze();

        context.DrawLine(pen, new Point(x, y), new Point(x + armX, y));
        context.DrawLine(pen, new Point(x, y), new Point(x, y + armY));
    }

    private void DrawSquareHandle(DrawingContext context, double centerX, double centerY)
    {
        var rect = new Rect(
            centerX - (HandleSize / 2),
            centerY - (HandleSize / 2),
            HandleSize,
            HandleSize);

        context.DrawRectangle(_sandwichInner, _handlePen, rect);
    }

    /// <summary>
    /// Badge com as dimensões, em fonte mono para os números não fazerem a caixa tremer
    /// enquanto o usuário arrasta.
    /// <para>
    /// A posição segue uma cascata: abaixo da seleção, acima se não couber, dentro se
    /// não couber em lugar nenhum. É o caso da seleção colada no topo da tela, que sem
    /// isso empurraria o badge para fora do monitor.
    /// </para>
    /// </summary>
    private void DrawDimensionBadge(DrawingContext context, PixelRect effective, Rect hole, Rect full)
    {
        // O multiplicador tipografico, e nao a letra x: e a diferenca entre parecer
        // desenhado e parecer digitado com pressa.
        string label = $"{effective.Width} × {effective.Height}";
        FormattedText text = Format(label, 11.5);

        const double padX = 8;
        const double padY = 4;
        const double gap = 8;
        double boxWidth = text.Width + (padX * 2);
        double boxHeight = text.Height + (padY * 2);

        double x = Math.Min(hole.Right - boxWidth, full.Width - boxWidth - 4);
        x = Math.Max(x, 4);

        double y = hole.Bottom + gap;
        if (y + boxHeight > full.Height - 4)
        {
            y = hole.Top - gap - boxHeight;
        }

        if (y < 4)
        {
            // Nem acima nem abaixo: cai para dentro da propria selecao.
            y = hole.Bottom - boxHeight - gap;
            y = Math.Max(y, hole.Top + gap);
        }

        var box = new Rect(x, y, boxWidth, boxHeight);
        context.DrawRoundedRectangle(_badgeBackground, null, box, 4, 4);
        context.DrawText(text, new Point(x + padX, y + padY));
    }

    /// <summary>Mira de ponta a ponta, enquanto não há nada selecionado.</summary>
    private void DrawCrosshair(DrawingContext context, Rect full)
    {
        (double cx, double cy) = ToLocal(_state.Cursor.X, _state.Cursor.Y);

        // O meio pixel alinha a linha a grade e evita que ela saia com dois pixels
        // borrados em vez de um nitido.
        double x = Math.Floor(cx) + 0.5;
        double y = Math.Floor(cy) + 0.5;

        context.DrawLine(_crosshairPen, new Point(0, y), new Point(full.Width, y));
        context.DrawLine(_crosshairPen, new Point(x, 0), new Point(x, full.Height));
    }

    /// <summary>
    /// Lupa com grade de pixels e o valor HEX sob o cursor.
    /// </summary>
    private void DrawMagnifier(DrawingContext context, Rect full)
    {
        int zoom = _state.MagnifierZoom;
        int sourceSide = Math.Max(3, (int)Math.Round(MagnifierSize / zoom));
        if (sourceSide % 2 == 0)
        {
            sourceSide++;
        }

        int half = sourceSide / 2;
        var source = new PixelRect(_state.Cursor.X - half, _state.Cursor.Y - half, sourceSide, sourceSide)
            .Intersect(_frozen.Bounds);

        if (source.IsEmpty)
        {
            return;
        }

        (double cx, double cy) = ToLocal(_state.Cursor.X, _state.Cursor.Y);
        double side = MagnifierSize;
        const double footer = MagnifierFooter;

        // Escolhe o quadrante com espaco, para a lupa nunca sair da tela.
        double x = cx + MagnifierGap;
        double y = cy + MagnifierGap;

        if (x + side > full.Width - 8)
        {
            x = cx - MagnifierGap - side;
        }

        if (y + side + footer > full.Height - 8)
        {
            y = cy - MagnifierGap - side - footer;
        }

        x = Math.Clamp(x, 8, Math.Max(8, full.Width - side - 8));
        y = Math.Clamp(y, 8, Math.Max(8, full.Height - side - footer - 8));

        var frame = new Rect(x, y, side, side + footer);
        context.DrawRoundedRectangle(_badgeBackground, _outerPen, frame, 8, 8);

        var viewport = new Rect(x, y, side, side);
        context.PushClip(new RectangleGeometry(viewport, 8, 8));

        var crop = new CroppedBitmap(
            _frozenBitmap,
            new Int32Rect(
                source.Left - _frozen.Bounds.Left,
                source.Top - _frozen.Bounds.Top,
                source.Width,
                source.Height));

        context.DrawImage(crop, viewport);

        double cell = side / sourceSide;
        if (zoom >= GridFromZoom)
        {
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), 1);
            grid.Freeze();

            for (int index = 1; index < sourceSide; index++)
            {
                double offset = Math.Floor(index * cell) + 0.5;
                context.DrawLine(grid, new Point(x + offset, y), new Point(x + offset, y + side));
                context.DrawLine(grid, new Point(x, y + offset), new Point(x + side, y + offset));
            }
        }

        // Celula central destacada: e o pixel exato sob a ponta do cursor.
        var center = new Rect(
            x + (Math.Floor(sourceSide / 2d) * cell),
            y + (Math.Floor(sourceSide / 2d) * cell),
            cell,
            cell);

        context.DrawRectangle(null, _accentPen, center);
        context.Pop();

        (byte r, byte g, byte b) = ReadPixel(_state.Cursor.X, _state.Cursor.Y);
        FormattedText hexText = Format($"#{r:X2}{g:X2}{b:X2}", 12);
        FormattedText positionText = Format($"{_state.Cursor.X}, {_state.Cursor.Y}", 11);

        var swatch = new Rect(x + 10, y + side + 8, 12, 12);
        var swatchBrush = new SolidColorBrush(Color.FromRgb(r, g, b));
        swatchBrush.Freeze();
        context.DrawRoundedRectangle(swatchBrush, _handlePen, swatch, 2, 2);

        context.DrawText(hexText, new Point(x + 28, y + side + 6));
        context.DrawText(positionText, new Point(x + 10, y + side + 24));
    }

    private (byte R, byte G, byte B) ReadPixel(int x, int y)
    {
        if (!_frozen.Bounds.Contains(x, y))
        {
            return (0, 0, 0);
        }

        int offset = ((y - _frozen.Bounds.Top) * _frozen.Stride)
                     + ((x - _frozen.Bounds.Left) * CapturedImage.BytesPerPixel);

        // Os bytes estao em BGRA.
        return (_frozen.Pixels[offset + 2], _frozen.Pixels[offset + 1], _frozen.Pixels[offset]);
    }

    private FormattedText Format(string text, double size) => new(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        _mono,
        size,
        _badgeText,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private bool CursorIsHere() => _monitor.Bounds.Contains(_state.Cursor.X, _state.Cursor.Y);

    /// <summary>Converte um retângulo físico para as coordenadas locais desta janela.</summary>
    private Rect ToLocal(PixelRect rect)
    {
        if (rect.IsEmpty)
        {
            return Rect.Empty;
        }

        (double left, double top) = ToLocal(rect.Left, rect.Top);
        return new Rect(left, top, rect.Width / _monitor.ScaleX, rect.Height / _monitor.ScaleY);
    }

    private (double X, double Y) ToLocal(int physicalX, int physicalY)
        => _monitor.ToDeviceIndependent(physicalX, physicalY);

    private static Rect Inflate(Rect rect, double amount)
    {
        double width = Math.Max(0, rect.Width + (amount * 2));
        double height = Math.Max(0, rect.Height + (amount * 2));
        return new Rect(rect.X - amount, rect.Y - amount, width, height);
    }

    private static Brush Resolve(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
