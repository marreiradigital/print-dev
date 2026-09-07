namespace PrintDev.Core.Screens;

/// <summary>
/// Retângulo em <b>pixels físicos</b> do desktop virtual.
/// <para>
/// Toda a geometria do Print Dev vive nesta unidade, e essa decisão elimina uma classe
/// inteira de defeitos. O processo é declarado PerMonitorV2 no manifesto, e sob PMv2 o
/// Windows não virtualiza DPI: <c>GetCursorPos</c>, <c>EnumDisplayMonitors</c> e o
/// contexto de dispositivo da tela já falam em pixels físicos. A conversão para as
/// unidades independentes de dispositivo do WPF acontece só na hora de desenhar, e só
/// no monitor em questão.
/// </para>
/// <para>
/// Misturar as duas unidades é o que faz um recorte sair deslocado quando o segundo
/// monitor tem escala diferente do primeiro.
/// </para>
/// </summary>
/// <param name="Left">Borda esquerda.</param>
/// <param name="Top">Borda superior.</param>
/// <param name="Width">Largura. Nunca negativa nos retângulos produzidos aqui.</param>
/// <param name="Height">Altura. Nunca negativa nos retângulos produzidos aqui.</param>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    /// <summary>Retângulo vazio, na origem.</summary>
    public static PixelRect Empty { get; }

    /// <summary>Borda direita, exclusiva.</summary>
    public int Right => Left + Width;

    /// <summary>Borda inferior, exclusiva.</summary>
    public int Bottom => Top + Height;

    /// <summary>Área em pixels.</summary>
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

    /// <summary>Verdadeiro quando não há nenhum pixel dentro.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Centro do retângulo, arredondado para baixo.</summary>
    public (int X, int Y) Center => (Left + (Width / 2), Top + (Height / 2));

    /// <summary>Monta a partir das quatro bordas, com a direita e a inferior exclusivas.</summary>
    public static PixelRect FromBounds(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>
    /// Monta a partir de dois pontos quaisquer, normalizando.
    /// É o que permite arrastar a seleção da direita para a esquerda, ou de baixo para
    /// cima, sem produzir largura negativa.
    /// </summary>
    public static PixelRect FromPoints(int x1, int y1, int x2, int y2)
        => FromBounds(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    /// <summary>Indica se o ponto está dentro (bordas direita e inferior exclusivas).</summary>
    public bool Contains(int x, int y)
        => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>Indica se o outro retângulo está inteiramente dentro deste.</summary>
    public bool Contains(PixelRect other)
        => !other.IsEmpty && other.Left >= Left && other.Top >= Top
           && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>Parte comum aos dois. Vazio quando não se tocam.</summary>
    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(Left, other.Left);
        int top = Math.Max(Top, other.Top);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);

        return right <= left || bottom <= top ? Empty : FromBounds(left, top, right, bottom);
    }

    /// <summary>Indica se os dois se tocam em pelo menos um pixel.</summary>
    public bool IntersectsWith(PixelRect other) => !Intersect(other).IsEmpty;

    /// <summary>
    /// Menor retângulo que contém os dois. Ignora o vazio, para que somar retângulos
    /// numa sequência não arraste a origem para o canto zero.
    /// </summary>
    public PixelRect Union(PixelRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        return FromBounds(
            Math.Min(Left, other.Left),
            Math.Min(Top, other.Top),
            Math.Max(Right, other.Right),
            Math.Max(Bottom, other.Bottom));
    }

    /// <summary>Move sem mudar o tamanho.</summary>
    public PixelRect Offset(int dx, int dy) => this with { Left = Left + dx, Top = Top + dy };

    /// <summary>
    /// Empurra para dentro dos limites sem mudar o tamanho, encolhendo só se não couber.
    /// É o que mantém a seleção dentro da tela quando o usuário arrasta para fora dela.
    /// </summary>
    public PixelRect ClampInside(PixelRect bounds)
    {
        if (bounds.IsEmpty)
        {
            return Empty;
        }

        int width = Math.Min(Width, bounds.Width);
        int height = Math.Min(Height, bounds.Height);
        int left = Math.Clamp(Left, bounds.Left, bounds.Right - width);
        int top = Math.Clamp(Top, bounds.Top, bounds.Bottom - height);

        return new PixelRect(left, top, width, height);
    }

    /// <summary>Garante um tamanho mínimo, crescendo para a direita e para baixo.</summary>
    public PixelRect EnsureMinimum(int minWidth, int minHeight)
        => this with { Width = Math.Max(Width, minWidth), Height = Math.Max(Height, minHeight) };

    /// <inheritdoc/>
    public override string ToString() => $"{Width}x{Height} em ({Left}, {Top})";
}
