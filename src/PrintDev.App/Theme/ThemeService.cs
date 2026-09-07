using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using PrintDev.Core.Configuration;
using Serilog;

namespace PrintDev.Theme;

/// <summary>
/// Troca o tema em tempo de execução e acompanha o do Windows.
/// </summary>
public sealed class ThemeService
{
    /// <summary>
    /// Posição reservada ao dicionário de tokens de cor em <c>App.xaml</c>.
    /// <para>
    /// Trocar o item nesta posição é o que faz o tema mudar: todo
    /// <c>DynamicResource</c> reresolve sozinho, sem recriar janela nenhuma. Por isso a
    /// posição é fixa e documentada nos dois lugares.
    /// </para>
    /// </summary>
    private const int TokenSlot = 0;

    private const string PersonalizeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    private readonly ILogger _log;
    private ThemeMode _requested = ThemeMode.Sistema;

    public ThemeService(ILogger log) => _log = log.ForContext<ThemeService>();

    /// <summary>Tema que está pintado agora.</summary>
    public bool IsDark { get; private set; } = true;

    /// <summary>
    /// Aplica o tema pedido. <see cref="ThemeMode.Sistema"/> lê a preferência do Windows.
    /// </summary>
    public void Apply(ThemeMode mode)
    {
        _requested = mode;
        bool dark = mode switch
        {
            ThemeMode.Claro => false,
            ThemeMode.Escuro => true,
            _ => ReadSystemPrefersDark(),
        };

        SwapTokens(dark);
    }

    /// <summary>
    /// Reavalia quando o Windows avisa que a cor do sistema mudou. Só faz efeito se o
    /// usuário escolheu acompanhar o sistema.
    /// </summary>
    public void OnSystemColorsChanged()
    {
        if (_requested == ThemeMode.Sistema)
        {
            Apply(ThemeMode.Sistema);
        }
    }

    private void SwapTokens(bool dark)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        var uri = new Uri(
            dark
                ? "pack://application:,,,/PrintDev;component/Theme/Tokens.Dark.xaml"
                : "pack://application:,,,/PrintDev;component/Theme/Tokens.Light.xaml",
            UriKind.Absolute);

        var merged = application.Resources.MergedDictionaries;

        if (merged.Count <= TokenSlot)
        {
            merged.Insert(TokenSlot, new ResourceDictionary { Source = uri });
        }
        else
        {
            merged[TokenSlot] = new ResourceDictionary { Source = uri };
        }

        IsDark = dark;
        _log.Information("Tema aplicado: {Tema}", dark ? "escuro" : "claro");
    }

    /// <summary>
    /// Lê a preferência do Windows para aplicativos. Ausente ou ilegível conta como
    /// claro, que é o padrão de uma instalação nova.
    /// </summary>
    public static bool ReadSystemPrefersDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightTheme) is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Confirma que as fontes embutidas foram encontradas.
    /// <para>
    /// Vale o cuidado porque o WPF <b>não avisa</b> quando não acha uma fonte: ele cai
    /// silenciosamente na fonte de reserva, e o defeito só aparece como "está estranho"
    /// muito depois. Cinco detalhes de sintaxe podem causar isso, e todos compilam.
    /// </para>
    /// </summary>
    public void VerifyFonts()
    {
        if (Application.Current is null)
        {
            return;
        }

        CheckFont("Font.Sans", "Archivo");
        CheckFont("Font.Mono", "JetBrains Mono");
    }

    private void CheckFont(string resourceKey, string expectedFamily)
    {
        if (Application.Current!.TryFindResource(resourceKey) is not FontFamily family)
        {
            _log.Warning("Recurso de fonte {Chave} não encontrado.", resourceKey);
            return;
        }

        bool resolved = family.FamilyNames.Values.Any(
            name => string.Equals(name, expectedFamily, StringComparison.OrdinalIgnoreCase));

        if (resolved)
        {
            _log.Debug("Fonte {Familia} carregada.", expectedFamily);
        }
        else
        {
            _log.Warning(
                "A fonte {Familia} NÃO foi carregada; o WPF caiu na fonte de reserva. Confira o Build Action e o nome da família.",
                expectedFamily);
        }
    }
}
