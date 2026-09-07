using System.Windows;
using System.Windows.Controls;

namespace PrintDev.Controls;

/// <summary>
/// Um agrupamento de configurações, com título opcional.
/// <para>
/// No tema escuro ele sobe com borda mais um brilho de um pixel no topo, e não com
/// sombra: sombra escura sobre fundo escuro não aparece, e ainda custa uma passada de
/// desfoque por quadro.
/// </para>
/// </summary>
public class Card : ContentControl
{
    /// <summary>Título do grupo. Vazio esconde o cabeçalho.</summary>
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(Card),
            new PropertyMetadata(null));

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }
}
