using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using PrintDev.Core.Configuration;
using Serilog;

namespace PrintDev.Theme;

/// <summary>
/// Troca o tema em tempo de execução e acompanha o do Windows.
/// </summary>
public sealed class ThemeService : IDisposable
{
    /// <summary>
    /// Espera depois do último aviso do Windows antes de reavaliar. Trocar o tema dispara
    /// uma rajada de avisos (cor, estilo visual, configurações gerais) e uma releitura do
    /// registro por aviso seria trabalho jogado fora.
    /// </summary>
    private static readonly TimeSpan SystemChangeDebounce = TimeSpan.FromMilliseconds(250);

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
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _debounce;

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
    /// Passa a acompanhar o tema do Windows. Precisa ser chamado na thread de interface.
    /// <para>
    /// O aviso vem de <see cref="SystemEvents.UserPreferenceChanged"/>, e não de uma
    /// janela do programa: o <c>WM_SETTINGCHANGE</c> é uma mensagem de difusão, e janela
    /// só de mensagens (<c>HWND_MESSAGE</c>, a que guarda os atalhos) nunca recebe
    /// difusão. Por ali o aviso de troca de tema simplesmente não chegava, e o programa
    /// só mudava de tema no próximo início.
    /// </para>
    /// </summary>
    public void WatchSystem()
    {
        if (_dispatcher is not null)
        {
            return;
        }

        _dispatcher = Application.Current.Dispatcher;

        _debounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = SystemChangeDebounce,
        };

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            OnSystemColorsChanged();
        };

        // Evento estatico: quem assina e nunca soltar vira vazamento do objeto inteiro,
        // e do Dispatcher junto. A saida esta no Dispose.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>
    /// O aviso chega numa thread própria do <see cref="SystemEvents"/>; o trabalho todo
    /// volta para a thread de interface.
    /// </summary>
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // O tema claro/escuro chega como "configuracoes gerais"; cor e estilo visual
        // vem junto em algumas versoes do Windows. O resto (papel de parede, mouse,
        // teclado...) nao interessa.
        if (e.Category is not (UserPreferenceCategory.General
            or UserPreferenceCategory.Color
            or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        _dispatcher?.BeginInvoke(() =>
        {
            // Reinicia a contagem: o ultimo aviso da rajada e que vale.
            _debounce?.Stop();
            _debounce?.Start();
        });
    }

    /// <summary>
    /// Reavalia quando o Windows avisa que a cor do sistema mudou. Só faz efeito se o
    /// usuário escolheu acompanhar o sistema e se o tema do Windows de fato mudou.
    /// </summary>
    public void OnSystemColorsChanged()
    {
        if (_requested != ThemeMode.Sistema)
        {
            return;
        }

        // "Configuracoes gerais" muda por centenas de motivos; so troca os tokens (e
        // loga) quando o tema em si mudou.
        if (ReadSystemPrefersDark() != IsDark)
        {
            Apply(ThemeMode.Sistema);
        }
    }

    /// <summary>Solta a assinatura do evento estático do sistema.</summary>
    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _debounce?.Stop();
        _debounce = null;
        _dispatcher = null;
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
