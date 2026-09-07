using System.Windows;
using System.Windows.Controls;

namespace PrintDev.Controls;

/// <summary>
/// Uma linha de configuração: rótulo à esquerda, controle à direita e uma descrição
/// opcional embaixo do rótulo.
/// <para>
/// Existe para que ninguém monte essa linha à mão. O ritmo — 4 pixels entre o rótulo e a
/// descrição, 20 e 14 de espaçamento interno — vive dentro do template, uma vez só. Para
/// deixar duas coisas coladas na tela, alguém precisaria editar o design system, o que é
/// exatamente a barreira que se quer.
/// </para>
/// <para>
/// O controle é alinhado ao meio do RÓTULO, e não ao meio da linha inteira: quando há
/// descrição, alinhar pelo centro da linha faz o interruptor "cair" e parecer solto.
/// </para>
/// </summary>
public class SettingRow : ContentControl
{
    /// <summary>Rótulo da configuração.</summary>
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(SettingRow),
            new PropertyMetadata(string.Empty));

    /// <summary>
    /// Explicação secundária. É EXCEÇÃO, não regra: se o rótulo precisa de descrição,
    /// vale primeiro tentar reescrever o rótulo.
    /// </summary>
    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingRow),
            new PropertyMetadata(null));

    /// <inheritdoc cref="LabelProperty"/>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <inheritdoc cref="DescriptionProperty"/>
    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
