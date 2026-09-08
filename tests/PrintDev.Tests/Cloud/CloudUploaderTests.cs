using System.IO;
using System.Net;
using System.Net.Http;
using PrintDev.Core.Cloud;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;

namespace PrintDev.Tests.Cloud;

/// <summary>
/// A única parte do programa que faz um arquivo do usuário sair da máquina. O que
/// interessa testar aqui não é o caminho feliz — é tudo que ela precisa RECUSAR.
/// </summary>
public class CloudUploaderTests : IDisposable
{
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), "printdev-nuvem", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly JsonSettingsService _settings;

    public CloudUploaderTests()
    {
        _paths = new AppPaths(
            roamingRoot: Path.Combine(_raiz, "Roaming"),
            localRoot: Path.Combine(_raiz, "Local"),
            picturesRoot: Path.Combine(_raiz, "Imagens"));

        _paths.EnsureCreated();

        _settings = new JsonSettingsService(_paths, Serilog.Core.Logger.None);
        _settings.Load();
    }

    public void Dispose()
    {
        _settings.Dispose();

        try
        {
            Directory.Delete(_raiz, recursive: true);
        }
        catch (IOException)
        {
            // Faxina de teste não pode derrubar a suíte.
        }

        GC.SuppressFinalize(this);
    }

    private const string RespostaBoa = """
        {"url":"https://exemplo/i/aaaaBBBBccccDDDD","id":"aaaaBBBBccccDDDD",
         "token":"tokenSecretoDeTeste","expiraEm":4102444800,"ttlSegundos":172800}
        """;

    private string ArquivoPng(long bytes = 64)
    {
        string caminho = Path.Combine(_raiz, $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(caminho, new byte[bytes]);
        return caminho;
    }

    private void Configurar(string? endereco = null, string? chave = "chave-de-teste")
        => _settings.Update(s => s with
        {
            Cloud = s.Cloud with
            {
                Endpoint = endereco ?? s.Cloud.Endpoint,
                Key = chave,
            },
        });

    private (CloudUploader Uploader, Espiao Espiao) Criar(
        HttpStatusCode status = HttpStatusCode.Created,
        string corpo = RespostaBoa)
    {
        var espiao = new Espiao(status, corpo);
        return (new CloudUploader(new HttpClient(espiao), _settings, Serilog.Core.Logger.None), espiao);
    }

    [Fact]
    public async Task Envia_e_devolve_o_link_com_o_token_de_exclusao()
    {
        Configurar();
        (CloudUploader uploader, Espiao espiao) = Criar();

        UploadOutcome resultado = await uploader.UploadAsync(ArquivoPng());

        Assert.True(resultado.Success, resultado.Message);
        Assert.NotNull(resultado.Link);
        Assert.Equal("aaaaBBBBccccDDDD", resultado.Link!.Id);
        Assert.Equal("tokenSecretoDeTeste", resultado.Link.Token);

        // A chave precisa realmente ter ido no cabeçalho, senão o serviço recusaria.
        Assert.Equal("chave-de-teste", espiao.Ultimo?.Headers.GetValues("x-chave").Single());
        Assert.Equal("image/png", espiao.UltimoTipo);
    }

    /// <summary>
    /// O endereço vem de um arquivo de configuração que a pessoa edita à mão. Um
    /// <c>http://</c> ali mandaria captura de tela em texto claro pela rede — e uma
    /// captura de tela é, por definição, o lugar onde o segredo estava.
    /// </summary>
    [Theory]
    [InlineData("http://printdev.marreira.dev/i")]
    [InlineData("ftp://algum-lugar/i")]
    [InlineData("nao-e-url")]
    [InlineData("")]
    public async Task Recusa_endereco_que_nao_seja_https(string endereco)
    {
        Configurar(endereco);
        (CloudUploader uploader, Espiao espiao) = Criar();

        UploadOutcome resultado = await uploader.UploadAsync(ArquivoPng());

        Assert.False(resultado.Success);
        Assert.Null(espiao.Ultimo);
    }

    /// <summary>Barra sobrando no fim do endereço não pode virar barra dupla na URL.</summary>
    [Theory]
    [InlineData("https://exemplo.com/i")]
    [InlineData("https://exemplo.com/i/")]
    [InlineData("https://exemplo.com/i///")]
    public async Task Monta_a_url_sem_barra_dupla(string endereco)
    {
        Configurar(endereco);
        (CloudUploader uploader, Espiao espiao) = Criar();

        await uploader.UploadAsync(ArquivoPng());

        Assert.Equal("https://exemplo.com/i/novo", espiao.Ultimo?.RequestUri?.ToString());
    }

    [Fact]
    public async Task Recusa_arquivo_que_nao_e_imagem()
    {
        Configurar();
        (CloudUploader uploader, Espiao espiao) = Criar();

        string texto = Path.Combine(_raiz, "coisa.txt");
        File.WriteAllText(texto, "nada");

        UploadOutcome resultado = await uploader.UploadAsync(texto);

        Assert.False(resultado.Success);
        Assert.Null(espiao.Ultimo);
    }

    [Fact]
    public async Task Recusa_arquivo_grande_demais_sem_gastar_a_rede()
    {
        Configurar();
        (CloudUploader uploader, Espiao espiao) = Criar();

        UploadOutcome resultado = await uploader.UploadAsync(ArquivoPng(CloudUploader.MaxBytes + 1));

        Assert.False(resultado.Success);
        Assert.Contains("limite", resultado.Message);
        Assert.Null(espiao.Ultimo);
    }

    [Fact]
    public async Task Arquivo_que_sumiu_falha_com_frase_e_nao_com_excecao()
    {
        Configurar();
        (CloudUploader uploader, _) = Criar();

        UploadOutcome resultado = await uploader.UploadAsync(Path.Combine(_raiz, "nao-existe.png"));

        Assert.False(resultado.Success);
        Assert.Contains("não existe mais", resultado.Message);
    }

    [Fact]
    public async Task Sem_chave_nem_tenta()
    {
        Configurar(chave: null);
        (CloudUploader uploader, Espiao espiao) = Criar();

        UploadOutcome resultado = await uploader.UploadAsync(ArquivoPng());

        // A compilação de teste não tem chave embutida, então não há de onde tirar uma.
        Assert.False(resultado.Success);
        Assert.False(uploader.EstaConfigurado);
        Assert.Null(espiao.Ultimo);
    }

    /// <summary>O erro do serviço, quando vem em JSON, é preferido à frase genérica.</summary>
    [Fact]
    public async Task Usa_a_explicacao_que_o_servico_mandou()
    {
        Configurar();
        (CloudUploader uploader, _) = Criar(
            HttpStatusCode.ServiceUnavailable,
            """{"erro":"O serviço atingiu o limite diário de envios."}""");

        UploadOutcome resultado = await uploader.UploadAsync(ArquivoPng());

        Assert.False(resultado.Success);
        Assert.Equal("O serviço atingiu o limite diário de envios.", resultado.Message);
    }

    /// <summary>
    /// O 429 vem do limite de borda, como página HTML — e não do Worker, que responde
    /// JSON. Sem este caminho, a pessoa veria um pedaço de HTML como mensagem de erro.
    /// </summary>
    [Fact]
    public async Task Resposta_que_nao_e_json_cai_na_frase_por_codigo()
    {
        Configurar();
        (CloudUploader uploader, _) = Criar((HttpStatusCode)429, "<html><body>error 1015</body></html>");

        UploadOutcome resultado = await uploader.UploadAsync(ArquivoPng());

        Assert.False(resultado.Success);
        Assert.Contains("Muitos envios seguidos", resultado.Message);
        Assert.DoesNotContain("<html>", resultado.Message);
    }

    [Fact]
    public async Task Excluir_manda_o_token_e_nao_a_chave()
    {
        Configurar("https://exemplo.com/i");
        (CloudUploader uploader, Espiao espiao) = Criar(HttpStatusCode.OK, """{"excluida":true}""");

        var link = new CloudLink(
            "https://exemplo.com/i/aaaaBBBBccccDDDD",
            "aaaaBBBBccccDDDD",
            "tokenSecretoDeTeste",
            DateTimeOffset.UtcNow.AddHours(48),
            "c:\\qualquer.png");

        UploadOutcome resultado = await uploader.DeleteAsync(link);

        Assert.True(resultado.Success, resultado.Message);
        Assert.Equal(HttpMethod.Delete, espiao.Ultimo?.Method);
        Assert.Equal("https://exemplo.com/i/aaaaBBBBccccDDDD", espiao.Ultimo?.RequestUri?.ToString());
        Assert.Equal("tokenSecretoDeTeste", espiao.Ultimo?.Headers.GetValues("x-token").Single());
        Assert.False(espiao.Ultimo!.Headers.Contains("x-chave"));
    }

    /// <summary>Devolve uma resposta fixa e guarda o pedido, para conferência.</summary>
    private sealed class Espiao(HttpStatusCode status, string corpo) : HttpMessageHandler
    {
        public HttpRequestMessage? Ultimo { get; private set; }

        public string? UltimoTipo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Ultimo = request;
            UltimoTipo = request.Content?.Headers.ContentType?.MediaType;

            // O corpo precisa ser lido antes do retorno: o chamador descarta o pedido.
            if (request.Content is not null)
            {
                await request.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(corpo) };
        }
    }
}
