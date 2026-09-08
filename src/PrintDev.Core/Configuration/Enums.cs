namespace PrintDev.Core.Configuration;

/// <summary>Formato de arquivo da captura salva em disco.</summary>
public enum ImageFormat
{
    /// <summary>Sem perda. Padrão: captura de tela tem texto e linha fina.</summary>
    Png,

    /// <summary>Com perda, arquivo menor. A qualidade é configurável.</summary>
    Jpeg,
}

/// <summary>Tema da interface.</summary>
public enum ThemeMode
{
    /// <summary>Segue o tema do Windows e reage quando ele muda.</summary>
    Sistema,
    Claro,
    Escuro,
}

/// <summary>Como as capturas são organizadas em subpastas por data.</summary>
public enum DateSubfolder
{
    Nenhuma,
    Ano,
    AnoMes,
    AnoMesDia,
}

/// <summary>O que fazer quando já existe arquivo com o nome escolhido.</summary>
public enum NameCollision
{
    /// <summary>Acrescenta _2, _3... Padrão, porque nunca perde captura.</summary>
    SufixoNumerico,

    /// <summary>Acrescenta os milissegundos ao nome.</summary>
    Milissegundos,

    /// <summary>Sobrescreve. Só para quem sabe o que está fazendo.</summary>
    Sobrescrever,
}

/// <summary>O que a captura coloca na área de transferência.</summary>
public enum ClipboardContent
{
    /// <summary>
    /// Imagem e caminho ao mesmo tempo, em formatos diferentes. É o coração do
    /// produto: o app de destino escolhe o que sabe consumir.
    /// </summary>
    ImagemECaminho,

    /// <summary>Só os formatos de imagem.</summary>
    Imagem,

    /// <summary>Só o texto do caminho.</summary>
    Caminho,
}

/// <summary>Como o caminho do arquivo é escrito ao ser colado como texto.</summary>
public enum PathTextFormat
{
    /// <summary>C:\Users\...\captura.png</summary>
    Windows,

    /// <summary>C:/Users/.../captura.png</summary>
    BarraNormal,

    /// <summary>/mnt/c/Users/.../captura.png</summary>
    Wsl,

    /// <summary>file:///C:/Users/.../captura.png</summary>
    UriFile,

    /// <summary>![captura](file:///C:/...)</summary>
    Markdown,

    /// <summary>Marcação de imagem em HTML.</summary>
    Html,

    /// <summary>Apenas captura.png</summary>
    SomenteNome,
}

/// <summary>Quando envolver o caminho em aspas.</summary>
public enum QuoteMode
{
    /// <summary>
    /// Só quando há espaço. Padrão: é o que faz o caminho colado num terminal
    /// funcionar sem o usuário ter que consertar na mão.
    /// </summary>
    QuandoTiverEspaco,
    Nunca,
    Sempre,
}

/// <summary>O que acontece logo depois de a área ser escolhida.</summary>
public enum PostCaptureAction
{
    /// <summary>Copia e sai do caminho. Padrão.</summary>
    CopiarEFechar,

    /// <summary>Mostra a barra de ações ao lado da seleção.</summary>
    BarraPosCaptura,

    /// <summary>Vai direto para a anotação.</summary>
    Anotar,
}

/// <summary>Modo inicial do seletor quando o overlay abre.</summary>
public enum CaptureMode
{
    Regiao,
    Janela,
    Monitor,
    TelaInteira,
}

/// <summary>
/// O que fazer quando outro capturador está segurando a tecla de atalho.
/// </summary>
public enum CompetitorPolicy
{
    /// <summary>
    /// Mostra quem está com a tecla e oferece encerrar. Padrão: encerrar processo
    /// alheio sem avisar é destrutivo demais para ser silencioso.
    /// </summary>
    Perguntar,

    /// <summary>Encerra o concorrente e assume a tecla, avisando depois.</summary>
    AssumirAutomaticamente,

    /// <summary>Não mexe em nada; cai no atalho alternativo e marca o conflito.</summary>
    SomenteAvisar,
}

/// <summary>Tratamento aplicado pela ferramenta de esconder conteúdo.</summary>
public enum RedactionStyle
{
    Pixelar,
    Desfocar,
    Tarja,
}

/// <summary>Formato do texto copiado pelo conta-gotas de cor.</summary>
public enum ColorFormat
{
    Hex,
    Rgb,
    Hsl,
    Hex0X,
}

/// <summary>O que fazer quando existe uma versão mais nova publicada.</summary>
public enum UpdateAction
{
    /// <summary>
    /// Baixa em segundo plano, confere o digesto e instala no momento em que o
    /// programa é fechado. Padrão: é a única opção que atualiza sem nunca
    /// interromper o que a pessoa está fazendo.
    /// </summary>
    InstalarAoSair,

    /// <summary>Só avisa que existe versão nova. Nada é baixado sem um clique.</summary>
    SomenteAvisar,

    /// <summary>
    /// Baixa e reinicia o programa assim que der — respeitando os momentos em que
    /// interromper seria destrutivo (captura em andamento, editor aberto, pin na tela).
    /// </summary>
    InstalarAutomaticamente,

    /// <summary>Não procura atualização nenhuma.</summary>
    Desligado,
}
