using System.IO;
using PrintDev.Core.Configuration;
using PrintDev.Core.Infrastructure;

namespace PrintDev.Tests.Configuration;

public class JsonSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printdev-testes", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public JsonSettingsServiceTests()
    {
        _paths = new AppPaths(
            roamingRoot: Path.Combine(_root, "Roaming"),
            localRoot: Path.Combine(_root, "Local"),
            picturesRoot: Path.Combine(_root, "Imagens"));

        _paths.EnsureCreated();
    }

    private JsonSettingsService CreateService()
        => new(_paths, Serilog.Core.Logger.None);

    [Fact]
    public void Primeira_execucao_cria_o_arquivo_com_os_padroes()
    {
        using JsonSettingsService service = CreateService();

        service.Load();

        Assert.True(File.Exists(_paths.SettingsFile));
        Assert.Equal(new AppSettings(), service.Current);
    }

    [Fact]
    public void Le_o_que_ja_estava_no_disco()
    {
        File.WriteAllText(_paths.SettingsFile, """
            { "versaoDoSchema": 1, "salvamento": { "formato": "jpeg", "qualidadeJpeg": 55 } }
            """);

        using JsonSettingsService service = CreateService();
        service.Load();

        Assert.Equal(ImageFormat.Jpeg, service.Current.Save.Format);
        Assert.Equal(55, service.Current.Save.JpegQuality);
    }

    [Fact]
    public void Arquivo_corrompido_e_posto_de_lado_e_o_programa_sobe_com_os_padroes()
    {
        File.WriteAllText(_paths.SettingsFile, "{ isto nao e json valido");

        using JsonSettingsService service = CreateService();
        service.Load();

        Assert.Equal(new AppSettings(), service.Current);

        // O arquivo do usuario nao pode simplesmente sumir: pode ter horas de ajuste.
        string[] guardados = Directory.GetFiles(_paths.SettingsDirectory, "settings.corrompido-*.json");
        Assert.Single(guardados);
        Assert.Contains("isto nao e json valido", File.ReadAllText(guardados[0]));
    }

    [Fact]
    public void Update_troca_o_valor_na_hora_e_avisa()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        AppSettings? avisado = null;
        service.Changed += (_, s) => avisado = s;

        service.Update(s => s with { Save = s.Save with { JpegQuality = 42 } });

        Assert.Equal(42, service.Current.Save.JpegQuality);
        Assert.NotNull(avisado);
        Assert.Equal(42, avisado.Save.JpegQuality);
    }

    [Fact]
    public void Update_que_nao_muda_nada_nao_avisa_nem_grava()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        bool avisou = false;
        service.Changed += (_, _) => avisou = true;

        service.Update(s => s with { Save = s.Save with { JpegQuality = s.Save.JpegQuality } });

        Assert.False(avisou);
    }

    [Fact]
    public void Flush_grava_o_que_estava_pendente()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        service.Update(s => s with { Save = s.Save with { Format = ImageFormat.Jpeg } });
        service.Flush();

        Assert.Contains("\"formato\": \"jpeg\"", File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void Dispose_grava_o_ajuste_que_ainda_nao_tinha_ido_para_o_disco()
    {
        // Encerrar o programa logo apos mexer numa configuracao nao pode perder o ajuste,
        // porque a gravacao e adiada de proposito.
        using (JsonSettingsService service = CreateService())
        {
            service.Load();
            service.Update(s => s with { History = s.History with { Count = 99 } });
        }

        Assert.Contains("\"quantidade\": 99", File.ReadAllText(_paths.SettingsFile));
    }

    [Fact]
    public void Gravacao_deixa_copia_de_seguranca_da_versao_anterior()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        service.Update(s => s with { History = s.History with { Count = 5 } });
        service.Flush();

        Assert.True(File.Exists(_paths.SettingsFile + ".bak"));
        // O temporario nunca pode sobrar: seria lixo permanente na pasta do usuario.
        Assert.False(File.Exists(_paths.SettingsFile + ".tmp"));
    }

    [Fact]
    public void Edicao_do_arquivo_por_fora_volta_para_o_programa()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        using var recarregou = new ManualResetEventSlim(false);
        service.Changed += (_, _) => recarregou.Set();

        File.WriteAllText(_paths.SettingsFile, """
            { "versaoDoSchema": 1, "historico": { "quantidade": 7 } }
            """);

        Assert.True(recarregou.Wait(TimeSpan.FromSeconds(10)), "O observador do arquivo não avisou a tempo.");
        Assert.Equal(7, service.Current.History.Count);
    }

    // ==================== Releitura x gravação ====================

    [Fact]
    public void Releitura_nao_desfaz_uma_mudanca_feita_logo_depois_de_gravar()
    {
        // O defeito: a gravação da mudança A dispara o observador, e a releitura (300 ms
        // depois) via o disco com A diferente da memória, onde já estava B feita nesse
        // intervalo. Revertia para A, e a gravação pendente escrevia A por cima de B.
        using JsonSettingsService service = CreateService();
        service.Load();

        service.Update(s => s with { History = s.History with { Count = 11 } });
        service.Flush();
        service.Update(s => s with { History = s.History with { Count = 22 } });

        service.ReloadFromDisk();

        Assert.Equal(22, service.Current.History.Count);

        service.Flush();
        Assert.Contains("\"quantidade\": 22", LerArquivo());
    }

    [Fact]
    public void O_eco_da_propria_gravacao_nao_vira_um_novo_aviso()
    {
        // Chave desconhecida vai para o Extras, que é um dicionário novo a cada leitura e
        // quebra a igualdade do record: sem reconhecer o eco pelo conteúdo, toda releitura
        // parecia uma mudança e avisava.
        File.WriteAllText(_paths.SettingsFile, """
            { "versaoDoSchema": 1, "futuro": { "x": 1 } }
            """);

        using JsonSettingsService service = CreateService();
        service.Load();

        int avisos = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref avisos);

        service.Update(s => s with { History = s.History with { Count = 5 } });
        service.Flush();
        service.ReloadFromDisk();

        Assert.Equal(1, Volatile.Read(ref avisos));
    }

    [Fact]
    public void Releitura_aplica_a_edicao_feita_por_fora_quando_nada_esta_pendente()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        File.WriteAllText(_paths.SettingsFile, """
            { "versaoDoSchema": 1, "historico": { "quantidade": 7 } }
            """);

        service.ReloadFromDisk();

        Assert.Equal(7, service.Current.History.Count);
    }

    [Fact]
    public void Edicao_por_fora_nao_atropela_uma_mudanca_ainda_nao_gravada()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        service.Update(s => s with { History = s.History with { Count = 5 } });
        File.WriteAllText(_paths.SettingsFile, """
            { "versaoDoSchema": 1, "historico": { "quantidade": 7 } }
            """);

        service.ReloadFromDisk();

        // A mudança feita no programa prevalece e é a que vai para o disco.
        Assert.Equal(5, service.Current.History.Count);
        service.Flush();
        Assert.Contains("\"quantidade\": 5", LerArquivo());
    }

    [Fact]
    public void Arquivo_que_sumiu_nao_devolve_a_configuracao_aos_padroes()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        service.Update(s => s with { History = s.History with { Count = 5 } });
        service.Flush();
        File.Delete(_paths.SettingsFile);

        service.ReloadFromDisk();

        Assert.Equal(5, service.Current.History.Count);
    }

    // ==================== Falha de gravação ====================

    [Fact]
    public void Gravacao_que_falha_continua_pendente_e_e_refeita_sozinha()
    {
        var tempos = new JsonSettingsService.Timings
        {
            SaveDebounce = TimeSpan.FromMilliseconds(30),
            SaveRetry = TimeSpan.FromMilliseconds(60),
        };

        using JsonSettingsService service = CreateService(tempos);
        service.Load();

        // Um diretório no lugar do arquivo temporário faz a gravação falhar de verdade,
        // sem depender de permissão de disco.
        string bloqueio = _paths.SettingsFile + ".tmp";
        Directory.CreateDirectory(bloqueio);

        service.Update(s => s with { History = s.History with { Count = 33 } });
        Thread.Sleep(400);

        Assert.DoesNotContain("\"quantidade\": 33", LerArquivo());

        Directory.Delete(bloqueio);

        Assert.True(
            SpinWait.SpinUntil(() => LerArquivo().Contains("\"quantidade\": 33"), TimeSpan.FromSeconds(10)),
            "A gravação que falhou não foi refeita sozinha.");
    }

    [Fact]
    public void Dispose_grava_o_que_uma_gravacao_anterior_nao_conseguiu()
    {
        // Antes, a marca de pendente caía ANTES de gravar: depois de uma falha o Dispose
        // achava que não havia nada a fazer e a mudança se perdia ao encerrar.
        var tempos = new JsonSettingsService.Timings { SaveDebounce = TimeSpan.FromMilliseconds(20) };

        string bloqueio = _paths.SettingsFile + ".tmp";

        using (JsonSettingsService service = CreateService(tempos))
        {
            service.Load();
            Directory.CreateDirectory(bloqueio);

            service.Update(s => s with { History = s.History with { Count = 44 } });
            Thread.Sleep(300);

            Directory.Delete(bloqueio);
        }

        Assert.Contains("\"quantidade\": 44", LerArquivo());
    }

    // ==================== Arquivo bloqueado na partida ====================

    [Fact]
    public void Arquivo_bloqueado_na_partida_nao_e_sobrescrito_com_os_padroes()
    {
        // Antes, uma leitura que falhava virava "padrões" em silêncio e a primeira
        // mudança gravava os padrões por cima do arquivo real do usuário.
        const string original = """
            { "versaoDoSchema": 1, "salvamento": { "formato": "jpeg" }, "historico": { "quantidade": 7 } }
            """;

        File.WriteAllText(_paths.SettingsFile, original);

        var tempos = new JsonSettingsService.Timings
        {
            LoadAttempts = 2,
            ReadAttemptDelay = TimeSpan.FromMilliseconds(10),
            SaveDebounce = TimeSpan.FromMilliseconds(20),
            UnreadableRetry = TimeSpan.FromHours(1),
        };

        using JsonSettingsService service = CreateService(tempos);

        using (new FileStream(_paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            service.Load();

            Assert.Equal(new AppSettings(), service.Current);

            service.Update(s => s with { History = s.History with { Count = 99 } });
            service.Flush();
            Thread.Sleep(100);
        }

        // Solto o arquivo, ele continua exatamente como estava.
        Assert.Equal(original, LerArquivo());

        service.ReloadFromDisk();

        // O conteúdo real entrou, e a mudança feita no meio tempo foi reaplicada por cima.
        Assert.Equal(ImageFormat.Jpeg, service.Current.Save.Format);
        Assert.Equal(99, service.Current.History.Count);

        service.Flush();
        string depois = LerArquivo();
        Assert.Contains("\"formato\": \"jpeg\"", depois);
        Assert.Contains("\"quantidade\": 99", depois);
    }

    // ==================== Ordem dos avisos ====================

    [Fact]
    public void Assinante_que_muda_a_configuracao_nao_vira_recursao_e_a_ordem_se_mantem()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        var vistos = new List<int>();
        int profundidade = 0;
        int maiorProfundidade = 0;

        service.Changed += (_, s) =>
        {
            maiorProfundidade = Math.Max(maiorProfundidade, ++profundidade);
            vistos.Add(s.History.Count);

            if (s.History.Count == 1)
            {
                service.Update(x => x with { History = x.History with { Count = 2 } });
            }

            profundidade--;
        };

        service.Update(s => s with { History = s.History with { Count = 1 } });

        Assert.Equal([1, 2], vistos);
        Assert.Equal(1, maiorProfundidade);
    }

    [Fact]
    public void Assinante_com_defeito_nao_impede_os_outros_nem_estraga_a_fila()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        int chegou = 0;
        service.Changed += (_, _) => throw new InvalidOperationException("assinante com defeito");
        service.Changed += (_, _) => chegou++;

        service.Update(s => s with { History = s.History with { Count = 1 } });
        service.Update(s => s with { History = s.History with { Count = 2 } });

        Assert.Equal(2, chegou);
    }

    [Fact]
    public async Task Com_varias_threads_o_ultimo_aviso_e_sempre_o_estado_final()
    {
        using JsonSettingsService service = CreateService();
        service.Load();

        AppSettings? ultimo = null;
        service.Changed += (_, s) => ultimo = s;

        await Task.WhenAll(Enumerable.Range(1, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                int valor = (t * 1000) + i;
                service.Update(s => s with { History = s.History with { Count = valor } });
            }
        })));

        Assert.NotNull(ultimo);
        Assert.Equal(service.Current, ultimo);
    }

    private JsonSettingsService CreateService(JsonSettingsService.Timings tempos)
        => new(_paths, Serilog.Core.Logger.None, tempos);

    /// <summary>
    /// Lê o arquivo, esperando a troca atômica da gravação terminar: durante o
    /// <c>File.Replace</c> ele fica bloqueado por instantes.
    /// </summary>
    private string LerArquivo()
    {
        for (int tentativa = 0; tentativa < 50; tentativa++)
        {
            try
            {
                return File.ReadAllText(_paths.SettingsFile);
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
        }

        return File.ReadAllText(_paths.SettingsFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
