namespace PrintDev.Core.Configuration;

/// <summary>
/// Guarda as configurações vivas do aplicativo e as mantém em sincronia com o disco
/// nos dois sentidos: o que a interface muda vai para o arquivo, e o que alguém edita
/// no arquivo volta para o aplicativo.
/// </summary>
public interface ISettingsService : IDisposable
{
    /// <summary>Configurações em vigor neste momento.</summary>
    AppSettings Current { get; }

    /// <summary>
    /// Disparado quando as configurações mudam, seja pela interface, seja porque
    /// alguém editou o arquivo. Vem de um thread qualquer: quem for tocar em
    /// interface precisa passar pelo dispatcher.
    /// </summary>
    event EventHandler<AppSettings>? Changed;

    /// <summary>
    /// Lê o arquivo. Nunca lança por causa do conteúdo: arquivo ausente vira padrões,
    /// arquivo corrompido é posto de lado e o aplicativo sobe mesmo assim.
    /// </summary>
    void Load();

    /// <summary>
    /// Aplica uma mudança e agenda a gravação. Se a função devolver algo igual ao
    /// que já valia, nada acontece — nem evento, nem escrita em disco.
    /// </summary>
    void Update(Func<AppSettings, AppSettings> change);

    /// <summary>Grava agora o que estiver pendente, sem esperar o agendamento.</summary>
    void Flush();
}
