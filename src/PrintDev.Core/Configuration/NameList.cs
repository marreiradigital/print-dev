using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintDev.Core.Configuration;

/// <summary>
/// Uma lista de nomes que se compara pelo conteúdo.
/// <para>
/// As configurações são records, e o serviço depende da igualdade deles: mudança que não
/// muda nada não grava, e o arquivo relido igual ao que está em memória não dispara
/// evento. Uma lista comum se compara por referência — a mesma lista relida do disco
/// pareceria sempre diferente, e cada gravação voltaria como "mudança feita por fora",
/// reaplicando atalhos e tema à toa.
/// </para>
/// </summary>
[JsonConverter(typeof(NameListJsonConverter))]
[CollectionBuilder(typeof(NameList), nameof(Create))]
public sealed class NameList : IReadOnlyList<string>, IEquatable<NameList>
{
    private readonly string[] _items;

    public NameList(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    /// <summary>Para a sintaxe de coleção: <c>NameList lista = ["a", "b"];</c></summary>
    public static NameList Create(ReadOnlySpan<string> items) => new(items.ToArray());

    /// <inheritdoc/>
    public int Count => _items.Length;

    /// <inheritdoc/>
    public string this[int index] => _items[index];

    /// <inheritdoc/>
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <inheritdoc/>
    public bool Equals(NameList? other)
        => other is not null && _items.AsSpan().SequenceEqual(other._items);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as NameList);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (string item in _items)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => string.Join(", ", _items);
}

/// <summary>Grava a lista como um array JSON comum.</summary>
internal sealed class NameListJsonConverter : JsonConverter<NameList>
{
    public override NameList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(JsonSerializer.Deserialize<string[]>(ref reader, options) ?? []);

    public override void Write(Utf8JsonWriter writer, NameList value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (string item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
