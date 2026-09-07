namespace PrintDev.Core.Configuration;

/// <summary>
/// Traz um arquivo de configurações antigo para o esquema atual.
/// <para>
/// Hoje só existe a versão 1, então a migração é a identidade. A estrutura já está
/// montada porque a alternativa — descobrir que precisa migrar no dia em que a
/// mudança acontece — costuma custar as configurações de alguém.
/// </para>
/// </summary>
public static class SettingsMigrator
{
    /// <summary>Aplica as migrações necessárias, em cadeia.</summary>
    /// <param name="settings">Configurações lidas do arquivo.</param>
    /// <param name="migrated">
    /// <see langword="true"/> quando algo mudou e vale regravar o arquivo.
    /// </param>
    public static AppSettings Migrate(AppSettings settings, out bool migrated)
    {
        ArgumentNullException.ThrowIfNull(settings);

        migrated = false;
        AppSettings current = settings;

        // Arquivo de uma versao FUTURA: nao rebaixar nem apagar nada. As chaves
        // desconhecidas ja estao guardadas em Extras e voltam intactas na regravacao.
        if (current.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            return current;
        }

        // Versao ausente ou zero: arquivo escrito a mao. Assume a versao 1 e segue.
        if (current.SchemaVersion < 1)
        {
            current = current with { SchemaVersion = 1 };
            migrated = true;
        }

        // As proximas migracoes entram aqui, uma por versao:
        //   if (current.SchemaVersion == 1) { current = De1Para2(current); migrated = true; }

        return current;
    }
}
