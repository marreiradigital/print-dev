namespace PrintDev.Core.Screens;

/// <summary>Onde o cursor pegou a seleção, no modo de ajuste.</summary>
public enum SelectionGrip
{
    /// <summary>Fora da seleção: arrastar começa outra.</summary>
    None,

    /// <summary>Dentro: arrastar move a seleção inteira.</summary>
    Inside,

    Left,
    Top,
    Right,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// As contas das alças da seleção, em pixels físicos.
/// <para>
/// Ficam fora da janela para poderem ser testadas: é aqui que mora o caso que todo
/// seletor erra uma vez — arrastar a borda esquerda para além da direita, que precisa
/// inverter a seleção em vez de produzir largura negativa.
/// </para>
/// </summary>
public static class SelectionGrips
{
    /// <summary>
    /// Qual alça está sob o ponto. Os cantos ganham das bordas, e as bordas ganham do
    /// miolo: é o que deixa pegar um canto de uma seleção pequena.
    /// </summary>
    /// <param name="selection">A seleção.</param>
    /// <param name="x">Cursor, em pixels físicos.</param>
    /// <param name="y">Cursor, em pixels físicos.</param>
    /// <param name="tolerance">Quantos pixels em volta de cada borda contam como ela.</param>
    public static SelectionGrip HitTest(PixelRect selection, int x, int y, int tolerance)
    {
        if (selection.IsEmpty)
        {
            return SelectionGrip.None;
        }

        int right = selection.Right - 1;
        int bottom = selection.Bottom - 1;

        bool insideX = x >= selection.Left - tolerance && x <= right + tolerance;
        bool insideY = y >= selection.Top - tolerance && y <= bottom + tolerance;

        if (!insideX || !insideY)
        {
            return SelectionGrip.None;
        }

        bool nearLeft = Math.Abs(x - selection.Left) <= tolerance;
        bool nearRight = Math.Abs(x - right) <= tolerance;
        bool nearTop = Math.Abs(y - selection.Top) <= tolerance;
        bool nearBottom = Math.Abs(y - bottom) <= tolerance;

        // Selecao mais estreita que duas tolerancias: as duas bordas "estao perto". Vale a
        // mais proxima, senao a da direita nunca poderia ser pega.
        if (nearLeft && nearRight)
        {
            nearLeft = Math.Abs(x - selection.Left) < Math.Abs(x - right);
            nearRight = !nearLeft;
        }

        if (nearTop && nearBottom)
        {
            nearTop = Math.Abs(y - selection.Top) < Math.Abs(y - bottom);
            nearBottom = !nearTop;
        }

        return (nearLeft, nearTop, nearRight, nearBottom) switch
        {
            (true, true, _, _) => SelectionGrip.TopLeft,
            (_, true, true, _) => SelectionGrip.TopRight,
            (true, _, _, true) => SelectionGrip.BottomLeft,
            (_, _, true, true) => SelectionGrip.BottomRight,
            (true, _, _, _) => SelectionGrip.Left,
            (_, true, _, _) => SelectionGrip.Top,
            (_, _, true, _) => SelectionGrip.Right,
            (_, _, _, true) => SelectionGrip.Bottom,
            _ => selection.Contains(x, y) ? SelectionGrip.Inside : SelectionGrip.None,
        };
    }

    /// <summary>
    /// A seleção depois de arrastar a alça pelo deslocamento indicado, sempre dentro de
    /// <paramref name="limits"/> e nunca menor que um pixel.
    /// </summary>
    /// <param name="start">A seleção quando o arrasto começou.</param>
    /// <param name="grip">A alça arrastada.</param>
    /// <param name="dx">Deslocamento horizontal desde o começo do arrasto.</param>
    /// <param name="dy">Deslocamento vertical desde o começo do arrasto.</param>
    /// <param name="limits">A área de onde a seleção não pode sair.</param>
    public static PixelRect Drag(PixelRect start, SelectionGrip grip, int dx, int dy, PixelRect limits)
    {
        if (grip == SelectionGrip.None || start.IsEmpty)
        {
            return start;
        }

        if (grip == SelectionGrip.Inside)
        {
            return start.Offset(dx, dy).ClampInside(limits);
        }

        int left = start.Left;
        int top = start.Top;
        int right = start.Right;
        int bottom = start.Bottom;

        if (grip is SelectionGrip.Left or SelectionGrip.TopLeft or SelectionGrip.BottomLeft)
        {
            left += dx;
        }

        if (grip is SelectionGrip.Right or SelectionGrip.TopRight or SelectionGrip.BottomRight)
        {
            right += dx;
        }

        if (grip is SelectionGrip.Top or SelectionGrip.TopLeft or SelectionGrip.TopRight)
        {
            top += dy;
        }

        if (grip is SelectionGrip.Bottom or SelectionGrip.BottomLeft or SelectionGrip.BottomRight)
        {
            bottom += dy;
        }

        // Passou da borda oposta: inverte, como um arrasto feito ao contrario.
        (left, right) = left <= right ? (left, right) : (right, left);
        (top, bottom) = top <= bottom ? (top, bottom) : (bottom, top);

        right = Math.Max(right, left + 1);
        bottom = Math.Max(bottom, top + 1);

        PixelRect moved = PixelRect.FromBounds(left, top, right, bottom).Intersect(limits);
        return moved.IsEmpty ? start : moved;
    }
}
