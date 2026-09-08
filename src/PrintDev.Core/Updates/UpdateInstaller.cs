using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using PrintDev.Core.Configuration;
using Serilog;

namespace PrintDev.Core.Updates;

/// <summary>Como terminou o download de um instalador.</summary>
/// <param name="Path">Onde o arquivo ficou, quando deu certo.</param>
public sealed record DownloadOutcome(bool Success, string? Path, string Message);

/// <summary>
/// Baixa o instalador, confere o digesto e o entrega ao Windows.
/// <para>
/// A conferência não é formalidade: sem ela, qualquer coisa que se meta entre o
/// programa e o GitHub passa a poder trocar um instalador por outro — e o programa
/// executaria com prazer, porque acabou de baixar "a atualização".
/// </para>
/// </summary>
public sealed class UpdateInstaller
{
    /// <summary>Teto de tamanho. Um instalador deste programa tem cerca de 7 MB.</summary>
    private const long MaxBytes = 200L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger _log;

    public UpdateInstaller(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log.ForContext<UpdateInstaller>();
    }

    /// <summary>Pasta dos instaladores baixados, fora de qualquer pasta do usuário.</summary>
    public static string DownloadDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "PrintDev", "atualizacao");

    /// <summary>
    /// Baixa e confere. Só devolve sucesso quando o arquivo em disco tem exatamente o
    /// digesto que a API do GitHub publicou para ele.
    /// </summary>
    public async Task<DownloadOutcome> DownloadAsync(
        AvailableUpdate update,
        IProgress<double>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        // Sem digesto não há instalação automática. A alternativa seria executar um
        // binário só porque ele veio de uma conexão que pareceu certa, o que é
        // exatamente a decisão que um atualizador não pode tomar sozinho.
        if (string.IsNullOrWhiteSpace(update.Sha256))
        {
            return new DownloadOutcome(
                false,
                null,
                "O lançamento não publicou o digesto do instalador, então não dá para conferir o download. Baixe manualmente pela página do projeto.");
        }

        if (update.SizeBytes > MaxBytes)
        {
            return new DownloadOutcome(false, null, "O instalador anunciado é grande demais para ser plausível.");
        }

        string destino = Path.Combine(DownloadDirectory, update.AssetName);
        string parcial = destino + ".parcial";

        // Teto generoso, mas teto: sem ele uma conexão que aceita a conexão e nunca
        // manda bytes deixaria a tarefa pendurada até o programa encerrar.
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limite.CancelAfter(TimeSpan.FromMinutes(10));
        CancellationToken token = limite.Token;

        try
        {
            Directory.CreateDirectory(DownloadDirectory);
            LimparAnteriores(destino);

            using (HttpResponseMessage resposta = await _http
                .GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false))
            {
                if (!resposta.IsSuccessStatusCode)
                {
                    return new DownloadOutcome(false, null, $"O download respondeu {(int)resposta.StatusCode}.");
                }

                long total = resposta.Content.Headers.ContentLength ?? update.SizeBytes;

                if (total > MaxBytes)
                {
                    return new DownloadOutcome(false, null, "O instalador é grande demais para ser plausível.");
                }

                await using Stream origem = await resposta.Content.ReadAsStreamAsync(token).ConfigureAwait(false);

                // O arquivo é escrito com nome de parcial e só assume o nome final
                // depois de conferido. Assim uma queda de conexão nunca deixa para
                // trás um "instalador" pela metade com o nome de um bom.
                await using (var arquivo = new FileStream(parcial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    long lidos = 0;
                    int n;

                    while ((n = await origem.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        lidos += n;

                        if (lidos > MaxBytes)
                        {
                            throw new InvalidDataException("O download passou do tamanho máximo aceito.");
                        }

                        await arquivo.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);

                        if (total > 0)
                        {
                            progress?.Report(Math.Min(1.0, (double)lidos / total));
                        }
                    }

                    await arquivo.FlushAsync(token).ConfigureAwait(false);
                }
            }

            string calculado = await CalcularSha256Async(parcial, token).ConfigureAwait(false);

            if (!string.Equals(calculado, update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warning(
                    "Digesto do instalador não confere. Esperado {Esperado}, obtido {Obtido}",
                    update.Sha256,
                    calculado);

                TentarApagar(parcial);

                return new DownloadOutcome(
                    false,
                    null,
                    "O instalador baixado não confere com o digesto publicado. O arquivo foi descartado.");
            }

            File.Move(parcial, destino, overwrite: true);

            _log.Information(
                "Instalador da {Versao} baixado e conferido em {Caminho}",
                update.Version.ToString(3),
                destino);

            return new DownloadOutcome(true, destino, $"Versão {update.Version.ToString(3)} pronta para instalar.");
        }
        catch (OperationCanceledException)
        {
            TentarApagar(parcial);
            throw;
        }
        catch (Exception excecao) when (excecao is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _log.Warning(excecao, "Falha ao baixar o instalador");
            TentarApagar(parcial);
            return new DownloadOutcome(false, null, "Não consegui baixar o instalador. Verifique a conexão.");
        }
    }

    /// <summary>
    /// Entrega o instalador ao Windows e devolve o controle. Quem chama precisa
    /// encerrar o programa <b>logo em seguida</b>: o instalador reconhece o mutex de
    /// instância única e não consegue substituir um executável em uso.
    /// </summary>
    /// <param name="installerPath">Arquivo já baixado e conferido.</param>
    /// <param name="settings">De onde saem as tarefas que o instalador deve repetir.</param>
    public bool Launch(string installerPath, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installerPath);
        ArgumentNullException.ThrowIfNull(settings);

        if (!File.Exists(installerPath))
        {
            _log.Warning("Instalador não está mais em {Caminho}", installerPath);
            return false;
        }

        try
        {
            var processo = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (string argumento in MontarArgumentos(settings))
            {
                processo.ArgumentList.Add(argumento);
            }

            Process.Start(processo);

            _log.Information("Instalador silencioso iniciado; encerrando para liberar o executável");
            return true;
        }
        catch (Exception excecao) when (excecao is System.ComponentModel.Win32Exception or IOException)
        {
            _log.Error(excecao, "Não consegui iniciar o instalador");
            return false;
        }
    }

    /// <summary>
    /// Argumentos da instalação silenciosa.
    /// <para>
    /// Os dois últimos existem por armadilhas reais e não por preciosismo:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <c>/DIR</c> fixa a pasta. Se o Print Dev estiver rodando elevado — que é o modo
    /// que faz o atalho global funcionar sobre janela de administrador — o instalador
    /// herda a elevação, passa a se considerar instalação de máquina e resolveria a
    /// pasta padrão para Arquivos de Programas, criando uma <b>segunda</b> instalação
    /// em vez de atualizar a que existe.
    /// </item>
    /// <item>
    /// <c>/MERGETASKS</c> repete a escolha de inicialização automática. Sem ele, uma
    /// instalação silenciosa usa o padrão da tarefa — que é marcada — e reativaria o
    /// início com o Windows para quem tinha desligado.
    /// </item>
    /// </list>
    /// </summary>
    private static IEnumerable<string> MontarArgumentos(AppSettings settings)
    {
        yield return "/VERYSILENT";
        yield return "/SUPPRESSMSGBOXES";
        yield return "/NORESTART";

        // Sinaliza ao instalador que ele deve religar o programa no fim. A entrada
        // normal de pós-instalação é pulada em modo silencioso.
        yield return "/ATUALIZACAO";

        yield return $"/DIR={AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}";

        // A entrada em HKCU\Run só vale quando o início automático é o comum. No modo
        // elevado quem sobe o programa é o Agendador de Tarefas, e as duas juntas
        // abririam o Print Dev duas vezes no logon.
        bool inicioPeloRegistro = settings.General.StartWithWindows && !settings.General.StartElevated;

        yield return inicioPeloRegistro ? "/MERGETASKS=autostart" : "/MERGETASKS=!autostart";
    }

    private static async Task<string> CalcularSha256Async(string path, CancellationToken cancellation)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] digesto = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);

        return Convert.ToHexString(digesto);
    }

    /// <summary>
    /// Apaga instaladores de tentativas anteriores. Sem isto, a pasta acumula um
    /// arquivo de sete megabytes por versão lançada, para sempre.
    /// </summary>
    private void LimparAnteriores(string manter)
    {
        try
        {
            foreach (string arquivo in Directory.EnumerateFiles(DownloadDirectory))
            {
                if (!string.Equals(arquivo, manter, StringComparison.OrdinalIgnoreCase))
                {
                    TentarApagar(arquivo);
                }
            }
        }
        catch (Exception excecao) when (excecao is IOException or UnauthorizedAccessException)
        {
            _log.Debug(excecao, "Não consegui limpar instaladores antigos");
        }
    }

    private void TentarApagar(string arquivo)
    {
        try
        {
            if (File.Exists(arquivo))
            {
                File.Delete(arquivo);
            }
        }
        catch (Exception excecao) when (excecao is IOException or UnauthorizedAccessException)
        {
            _log.Debug(excecao, "Não consegui apagar {Arquivo}", arquivo);
        }
    }
}
