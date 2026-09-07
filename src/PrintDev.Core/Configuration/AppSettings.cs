using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintDev.Core.Configuration;

/// <summary>
/// Todas as configurações do Print Dev.
/// <para>
/// O código é em inglês, mas as chaves do JSON são em português: o arquivo é feito
/// para ser editado à mão pelo dono da máquina, e ninguém deveria precisar traduzir
/// nada para mexer nas próprias preferências.
/// </para>
/// <para>
/// Todas as seções são <c>record</c> imutáveis. Trocar uma configuração produz um
/// objeto novo, o que elimina a classe de defeito em que metade do programa já leu o
/// valor antigo e a outra metade já leu o novo.
/// </para>
/// </summary>
public sealed record AppSettings
{
    /// <summary>Versão de esquema que este código sabe escrever.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("versaoDoSchema")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("geral")]
    public GeneralSettings General { get; init; } = new();

    [JsonPropertyName("captura")]
    public CaptureSettings Capture { get; init; } = new();

    [JsonPropertyName("salvamento")]
    public SaveSettings Save { get; init; } = new();

    [JsonPropertyName("areaDeTransferencia")]
    public ClipboardSettings Clipboard { get; init; } = new();

    [JsonPropertyName("atalhos")]
    public HotkeySettings Hotkeys { get; init; } = new();

    [JsonPropertyName("anotacao")]
    public AnnotationSettings Annotation { get; init; } = new();

    [JsonPropertyName("historico")]
    public HistorySettings History { get; init; } = new();

    [JsonPropertyName("limpeza")]
    public CleanupSettings Cleanup { get; init; } = new();

    [JsonPropertyName("cor")]
    public ColorSettings Color { get; init; } = new();

    [JsonPropertyName("avancado")]
    public AdvancedSettings Advanced { get; init; } = new();

    /// <summary>
    /// Chaves que este código não conhece, preservadas na regravação.
    /// Sem isso, abrir um arquivo escrito por uma versão mais nova apagaria as
    /// configurações dela em silêncio.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extras { get; init; }
}

/// <summary>Comportamento geral do programa.</summary>
public sealed record GeneralSettings
{
    [JsonPropertyName("iniciarComOWindows")]
    public bool StartWithWindows { get; init; }

    /// <summary>
    /// Iniciar elevado, via Agendador de Tarefas. É o que faz o atalho global
    /// funcionar mesmo com uma janela de administrador em primeiro plano.
    /// </summary>
    [JsonPropertyName("iniciarElevado")]
    public bool StartElevated { get; init; }

    [JsonPropertyName("tema")]
    public ThemeMode Theme { get; init; } = ThemeMode.Sistema;

    [JsonPropertyName("mostrarBoasVindas")]
    public bool ShowWelcome { get; init; } = true;
}

/// <summary>Como a captura se comporta.</summary>
public sealed record CaptureSettings
{
    [JsonPropertyName("modoPadraoDoSeletor")]
    public CaptureMode DefaultMode { get; init; } = CaptureMode.Regiao;

    [JsonPropertyName("opacidadeDoEscurecimento")]
    public double VeilOpacity { get; init; } = 0.55;

    [JsonPropertyName("mostrarLupa")]
    public bool ShowMagnifier { get; init; } = true;

    [JsonPropertyName("zoomDaLupa")]
    public int MagnifierZoom { get; init; } = 10;

    [JsonPropertyName("mostrarLinhasGuia")]
    public bool ShowCrosshair { get; init; } = true;

    [JsonPropertyName("mostrarDimensoes")]
    public bool ShowDimensions { get; init; } = true;

    [JsonPropertyName("incluirCursor")]
    public bool IncludeCursor { get; init; }

    [JsonPropertyName("atrasoAntesDeCapturarMs")]
    public int DelayBeforeCaptureMs { get; init; }

    [JsonPropertyName("somDeCaptura")]
    public bool PlaySound { get; init; }

    [JsonPropertyName("mostrarAviso")]
    public bool ShowToast { get; init; } = true;

    [JsonPropertyName("segundosDoAviso")]
    public double ToastSeconds { get; init; } = 4;

    [JsonPropertyName("lembrarUltimaRegiao")]
    public bool RememberLastRegion { get; init; } = true;
}

/// <summary>Onde e como o arquivo é gravado.</summary>
public sealed record SaveSettings
{
    /// <summary>
    /// Pasta de destino. Vazio significa a pasta padrão em Imagens, resolvida em
    /// tempo de execução — gravar o caminho absoluto aqui quebraria o arquivo de
    /// configuração ao ser copiado para outra máquina ou outro usuário.
    /// </summary>
    [JsonPropertyName("pasta")]
    public string Folder { get; init; } = string.Empty;

    [JsonPropertyName("subpastaPorData")]
    public DateSubfolder DateSubfolder { get; init; } = DateSubfolder.Nenhuma;

    [JsonPropertyName("formato")]
    public ImageFormat Format { get; init; } = ImageFormat.Png;

    [JsonPropertyName("qualidadeJpeg")]
    public int JpegQuality { get; init; } = 92;

    [JsonPropertyName("modeloDeNome")]
    public string NameTemplate { get; init; } = "PrintDev_{ano}-{mes}-{dia}_{hora}{min}{seg}";

    [JsonPropertyName("aoColidirNome")]
    public NameCollision OnNameCollision { get; init; } = NameCollision.SufixoNumerico;

    [JsonPropertyName("acaoAposCapturar")]
    public PostCaptureAction PostCaptureAction { get; init; } = PostCaptureAction.CopiarEFechar;

    [JsonPropertyName("abrirPastaAposSalvar")]
    public bool OpenFolderAfterSave { get; init; }
}

/// <summary>O coração do produto: o que vai para a área de transferência.</summary>
public sealed record ClipboardSettings
{
    [JsonPropertyName("copiarAutomaticamente")]
    public bool CopyAutomatically { get; init; } = true;

    [JsonPropertyName("conteudo")]
    public ClipboardContent Content { get; init; } = ClipboardContent.ImagemECaminho;

    [JsonPropertyName("formatoDoCaminho")]
    public PathTextFormat PathFormat { get; init; } = PathTextFormat.Windows;

    [JsonPropertyName("aspas")]
    public QuoteMode Quotes { get; init; } = QuoteMode.QuandoTiverEspaco;

    /// <summary>Raiz do WSL. Barra-mnt-barra no WSL; apenas a barra no Git Bash.</summary>
    [JsonPropertyName("raizWsl")]
    public string WslRoot { get; init; } = "/mnt/";

    /// <summary>
    /// Publicar também o formato de arquivo (CF_HDROP). Desligado por padrão: o
    /// Chromium já expõe a imagem da área de transferência como arquivo sozinho,
    /// então o ganho é pequeno, enquanto o custo é o Outlook anexar em vez de
    /// embutir a imagem no corpo do e-mail.
    /// </summary>
    [JsonPropertyName("incluirArquivo")]
    public bool IncludeFileDrop { get; init; }

    /// <summary>
    /// Publicar o formato HTML. Desligado: em campo de texto rico o Chromium
    /// prefere o HTML à imagem, e uma marcação apontando para o protocolo de
    /// arquivo local resulta em imagem quebrada na tela.
    /// </summary>
    [JsonPropertyName("incluirHtml")]
    public bool IncludeHtml { get; init; }

    [JsonPropertyName("modeloMarkdown")]
    public string MarkdownTemplate { get; init; } = "![{nome}]({caminho})";

    [JsonPropertyName("modeloHtml")]
    public string HtmlTemplate { get; init; } = "<img src={aspa}{caminho}{aspa} alt={aspa}{nome}{aspa}>";
}

/// <summary>Atalhos globais, no formato aceito pelo interpretador de atalho.</summary>
public sealed record HotkeySettings
{
    [JsonPropertyName("capturar")]
    public string Capture { get; init; } = "PrtSc";

    [JsonPropertyName("telaInteira")]
    public string FullScreen { get; init; } = "Ctrl+PrtSc";

    [JsonPropertyName("janelaAtiva")]
    public string ActiveWindow { get; init; } = "Shift+PrtSc";

    [JsonPropertyName("repetirUltimaRegiao")]
    public string RepeatLastRegion { get; init; } = "Ctrl+Shift+PrtSc";

    [JsonPropertyName("colarComoCaminho")]
    public string PasteAsPath { get; init; } = "Ctrl+Shift+V";

    [JsonPropertyName("colarComoImagem")]
    public string PasteAsImage { get; init; } = "Ctrl+Alt+V";

    [JsonPropertyName("contaGotas")]
    public string ColorPicker { get; init; } = "Ctrl+Alt+P";

    [JsonPropertyName("reconhecerTexto")]
    public string Ocr { get; init; } = "Ctrl+Alt+T";
}

/// <summary>Ferramentas de anotação.</summary>
public sealed record AnnotationSettings
{
    [JsonPropertyName("cor")]
    public string Color { get; init; } = "#FF3B30";

    [JsonPropertyName("espessura")]
    public double Thickness { get; init; } = 3;

    [JsonPropertyName("tamanhoDaFonte")]
    public double FontSize { get; init; } = 18;

    [JsonPropertyName("estiloDeOcultacao")]
    public RedactionStyle RedactionStyle { get; init; } = RedactionStyle.Pixelar;

    [JsonPropertyName("intensidadeDaOcultacao")]
    public int RedactionStrength { get; init; } = 12;

    [JsonPropertyName("salvarComoNovoArquivo")]
    public bool SaveAsNewFile { get; init; }
}

/// <summary>Histórico das capturas recentes.</summary>
public sealed record HistorySettings
{
    [JsonPropertyName("quantidade")]
    public int Count { get; init; } = 20;

    [JsonPropertyName("mostrarNaBandeja")]
    public bool ShowInTray { get; init; } = true;

    [JsonPropertyName("tamanhoDaMiniatura")]
    public int ThumbnailSize { get; init; } = 160;
}

/// <summary>Limpeza automática de capturas antigas.</summary>
public sealed record CleanupSettings
{
    /// <summary>Desligada por padrão. Apagar arquivo do usuário exige pedido dele.</summary>
    [JsonPropertyName("ativa")]
    public bool Enabled { get; init; }

    [JsonPropertyName("manterDias")]
    public int KeepDays { get; init; } = 30;

    [JsonPropertyName("tamanhoMaximoMb")]
    public int MaxSizeMb { get; init; } = 2048;

    /// <summary>Manda para a Lixeira em vez de apagar de vez.</summary>
    [JsonPropertyName("moverParaLixeira")]
    public bool MoveToRecycleBin { get; init; } = true;

    /// <summary>
    /// Trava de segurança: só remove arquivo cujo nome casa com o modelo do Print
    /// Dev. Se o usuário apontar a pasta de capturas para uma pasta que já tem
    /// coisa dele, nada que não seja nosso pode ser tocado.
    /// </summary>
    [JsonPropertyName("somenteArquivosDoPrintDev")]
    public bool OnlyPrintDevFiles { get; init; } = true;
}

/// <summary>Conta-gotas de cor.</summary>
public sealed record ColorSettings
{
    [JsonPropertyName("formatoPadrao")]
    public ColorFormat Format { get; init; } = ColorFormat.Hex;

    [JsonPropertyName("hexEmMaiusculas")]
    public bool UppercaseHex { get; init; }
}

/// <summary>Ajustes que a maioria nunca toca.</summary>
public sealed record AdvancedSettings
{
    /// <summary>Verbose, Debug, Information, Warning ou Error.</summary>
    [JsonPropertyName("nivelDeLog")]
    public string LogLevel { get; init; } = "Information";

    [JsonPropertyName("diasDeLog")]
    public int LogRetentionDays { get; init; } = 7;

    [JsonPropertyName("aoDetectarConcorrente")]
    public CompetitorPolicy OnCompetitorDetected { get; init; } = CompetitorPolicy.Perguntar;

    /// <summary>
    /// Vigia o atalho e tenta reassumir a tecla se um concorrente tomá-la depois
    /// que o Print Dev já estava rodando.
    /// </summary>
    [JsonPropertyName("vigiarAtalho")]
    public bool GuardHotkey { get; init; } = true;
}
