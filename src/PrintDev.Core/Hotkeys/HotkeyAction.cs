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

    /// <summary>
    /// Desfaz a captura mais recente: manda o arquivo para a Lixeira, tira do histórico e
    /// limpa a área de transferência.
    /// <para>
    /// Existe como atalho <b>global</b> por um motivo concreto: o aviso de captura não
    /// rouba o foco — e não deve —, então nenhuma tecla chega até ele. Sem isto, capturar
    /// por engano só tinha conserto pelo mouse, dentro dos quatro segundos do aviso.
    /// </para>
    /// </summary>
    UndoLastCapture,

    /// <summary>
    /// Publica a captura mais recente na nuvem e copia o link.
    /// <para>
    /// Existe porque o aviso some em segundos: sem um atalho, perder a janela do
    /// aviso obrigaria a capturar tudo de novo so para poder enviar.
    /// </para>
    /// </summary>
    SendToCloud,
}
