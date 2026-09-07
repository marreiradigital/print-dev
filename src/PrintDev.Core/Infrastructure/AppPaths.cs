using System.IO;

namespace PrintDev.Core.Infrastructure;

/// <summary>
/// Onde o Print Dev guarda cada coisa no disco.
/// </summary>
public interface IAppPaths
{
    /// <summary>Pasta de configuração do usuário. Acompanha o perfil (Roaming).</summary>
    string SettingsDirectory { get; }

    /// <summary>Arquivo de configuração.</summary>
    string SettingsFile { get; }

    /// <summary>Pasta dos arquivos de log, um por dia.</summary>
    string LogsDirectory { get; }

    /// <summary>
    /// Cache de miniaturas do histórico. Fica em Local, não em Roaming: é conteúdo
    /// derivado e pesado, que não faz sentido sincronizar com o perfil.
    /// </summary>
    string CacheDirectory { get; }

    /// <summary>Pasta padrão das capturas, usada enquanto o usuário não escolhe outra.</summary>
    string DefaultCapturesDirectory { get; }

    /// <summary>
    /// Destino de emergência quando a pasta configurada some (drive de rede caiu,
    /// pendrive removido). A captura nunca pode ser perdida por causa disso.
    /// </summary>
    string FallbackCapturesDirectory { get; }

    /// <summary>Cria as pastas que precisam existir antes do primeiro uso.</summary>
    void EnsureCreated();
}

/// <inheritdoc cref="IAppPaths"/>
public sealed class AppPaths : IAppPaths
{
    /// <summary>Nome da pasta do produto, repetido em Roaming, Local e Imagens.</summary>
    public const string ProductFolderName = "PrintDev";

    private readonly string _roamingRoot;
    private readonly string _localRoot;
    private readonly string _picturesRoot;

    /// <summary>Cria os caminhos a partir das pastas conhecidas do Windows.</summary>
    public AppPaths()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
    {
    }

    /// <summary>
    /// Construtor com as raízes explícitas. Existe para os testes poderem apontar
    /// tudo para uma pasta temporária sem tocar no perfil real do usuário.
    /// </summary>
    public AppPaths(string roamingRoot, string localRoot, string picturesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roamingRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(localRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(picturesRoot);

        _roamingRoot = roamingRoot;
        _localRoot = localRoot;
        _picturesRoot = picturesRoot;

        SettingsDirectory = Path.Combine(_roamingRoot, ProductFolderName);
        SettingsFile = Path.Combine(SettingsDirectory, "settings.json");
        LogsDirectory = Path.Combine(SettingsDirectory, "logs");
        CacheDirectory = Path.Combine(_localRoot, ProductFolderName, "cache");
        DefaultCapturesDirectory = Path.Combine(_picturesRoot, ProductFolderName);
        FallbackCapturesDirectory = Path.Combine(_localRoot, ProductFolderName, "capturas");
    }

    /// <inheritdoc/>
    public string SettingsDirectory { get; }

    /// <inheritdoc/>
    public string SettingsFile { get; }

    /// <inheritdoc/>
    public string LogsDirectory { get; }

    /// <inheritdoc/>
    public string CacheDirectory { get; }

    /// <inheritdoc/>
    public string DefaultCapturesDirectory { get; }

    /// <inheritdoc/>
    public string FallbackCapturesDirectory { get; }

    /// <inheritdoc/>
    public void EnsureCreated()
    {
        // A pasta de capturas NAO entra aqui de proposito: ela so e criada quando a
        // primeira captura acontece, para o app nao poluir Imagens de quem instalou,
        // abriu e desistiu.
        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
