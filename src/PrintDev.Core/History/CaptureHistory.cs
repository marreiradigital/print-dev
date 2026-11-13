using System.IO;
using System.Windows.Media.Imaging;
using PrintDev.Core.Capture;
using PrintDev.Core.Configuration;

namespace PrintDev.Core.History;

/// <summary>Uma captura que já aconteceu nesta sessão.</summary>
/// <param name="Path">Onde o arquivo ficou. Nulo quando a gravação falhou.</param>
/// <param name="Thumbnail">Miniatura já congelada, pronta para o menu.</param>
/// <param name="Width">Largura original.</param>
/// <param name="Height">Altura original.</param>
/// <param name="When">Quando aconteceu.</param>
public sealed record CaptureHistoryItem(
    string? Path,
    BitmapSource Thumbnail,
    int Width,
    int Height,
    DateTime When)
{
    /// <summary>Nome do arquivo, para mostrar no menu.</summary>
    public string DisplayName => Path is null
        ? $"{Width}×{Height} (não salva)"
        : System.IO.Path.GetFileName(Path);

    /// <summary>Verdadeiro quando o arquivo ainda existe em disco.</summary>
    public bool StillExists => Path is not null && File.Exists(Path);
}

/// <summary>
/// Guarda as capturas recentes da sessão.
/// <para>
/// Na prática, a captura que se quer de novo é quase sempre a anterior — foi copiada,
/// colada no lugar errado, e o clipboard já foi sobrescrito por outra coisa. Sem
/// histórico, o caminho é abrir a pasta e procurar pelo horário.
/// </para>
/// <para>
/// Vive só em memória: é atalho de sessão, não catálogo. Os arquivos continuam no disco,
/// que é onde o histórico de verdade mora.
/// </para>
/// </summary>
public sealed class CaptureHistory
{
    private readonly ISettingsService _settings;
    private readonly object _gate = new();
    private readonly LinkedList<CaptureHistoryItem> _items = [];

    public CaptureHistory(ISettingsService settings) => _settings = settings;

    /// <summary>Disparado quando o histórico muda, para o menu se redesenhar.</summary>
    public event EventHandler? Changed;

    /// <summary>As capturas, da mais recente para a mais antiga.</summary>
    public IReadOnlyList<CaptureHistoryItem> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>A captura mais recente, ou nulo se ainda não houve nenhuma.</summary>
    public CaptureHistoryItem? Latest
    {
        get
        {
            lock (_gate)
            {
                return _items.First?.Value;
            }
        }
    }

    /// <summary>Registra uma captura.</summary>
    public void Add(CapturedImage image, string? path)
    {
        ArgumentNullException.ThrowIfNull(image);

        HistorySettings settings = _settings.Current.History;
        var item = new CaptureHistoryItem(
            path,
            CreateThumbnail(image, settings.ThumbnailSize),
            image.Width,
            image.Height,
            DateTime.Now);

        lock (_gate)
        {
            // Regravar um arquivo (o Salvar do editor) nao e uma captura a mais: a
            // entrada antiga, com a miniatura sem as marcas, sai da lista.
            if (path is not null)
            {
                LinkedListNode<CaptureHistoryItem>? node = _items.First;
                while (node is not null)
                {
                    LinkedListNode<CaptureHistoryItem>? next = node.Next;
                    if (string.Equals(node.Value.Path, path, StringComparison.OrdinalIgnoreCase))
                    {
                        _items.Remove(node);
                    }

                    node = next;
                }
            }

            _items.AddFirst(item);

            while (_items.Count > Math.Max(1, settings.Count))
            {
                _items.RemoveLast();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tira uma captura do histórico, depois de o arquivo dela sumir.</summary>
    public void Remove(CaptureHistoryItem item)
    {
        lock (_gate)
        {
            _items.Remove(item);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Esvazia o histórico. Não toca em arquivo nenhum.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reduz a captura para a miniatura do menu.
    /// <para>
    /// Guardar a imagem inteira seria caro: uma captura de tela cheia passa de oito
    /// megabytes, e o histórico guarda vinte delas por padrão. A miniatura é congelada
    /// para poder ser usada de qualquer thread.
    /// </para>
    /// <para>
    /// A redução acontece nos bytes, ANTES de virar bitmap. A versão anterior usava um
    /// <c>TransformedBitmap</c> sobre a imagem inteira — e ele guarda a origem para
    /// reduzir só na hora de desenhar, então cada miniatura segurava a captura completa
    /// em memória nativa: vinte telas cheias para mostrar vinte ícones de 72 pixels.
    /// </para>
    /// </summary>
    internal static BitmapSource CreateThumbnail(CapturedImage image, int maxSide)
    {
        int side = Math.Clamp(maxSide, 48, 512);
        return image.Downscale(side).ToBitmapSource();
    }
}
