namespace PrintDev.Core.Hotkeys;

/// <summary>
/// O que cada atalho global faz. É a chave que liga a configuração ao comportamento.
/// </summary>
public enum HotkeyAction
{
    /// <summary>Abre o seletor de área. É o atalho principal, na tecla PrtSc.</summary>
    Capture,

    /// <summary>Captura o monitor sob o cursor, sem passar pelo seletor.</summary>
    FullScreen,

    /// <summary>Captura a janela em primeiro plano.</summary>
    ActiveWindow,

    /// <summary>
    /// Repete o último recorte, na mesma posição. Serve para acompanhar um erro ou uma
    /// compilação que muda dentro da mesma área da tela.
    /// </summary>
    RepeatLastRegion,

    /// <summary>Cola a última captura como caminho de texto, sem depender do destino.</summary>
    PasteAsPath,

    /// <summary>Cola a última captura como imagem, sem depender do destino.</summary>
    PasteAsImage,

    /// <summary>Abre o conta-gotas de cor.</summary>
    ColorPicker,

    /// <summary>Recorta uma área e copia o texto reconhecido nela.</summary>
    Ocr,
}
