using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;

namespace PrintDev.Core.Updates;

/// <summary>
/// Traduz a etiqueta de um lançamento do GitHub (<c>v0.1.3</c>) em número comparável.
/// <para>
/// Lógica pura de propósito. É a decisão mais perigosa de todo o sistema de
/// atualização: errar a comparação para o lado errado faz o programa se reinstalar em
/// laço, ou nunca mais se atualizar. As duas falhas são silenciosas.
/// </para>
/// </summary>
public static class ReleaseTag
{
    /// <summary>
    /// Versão deste executável, com três componentes.
    /// <para>
    /// O <c>&lt;Version&gt;</c> do projeto vira quatro números no assembly
    /// (<c>0.1.2.0</c>), enquanto a etiqueta do lançamento tem três. Comparar os
    /// quatro faria <c>0.1.2.0</c> nunca ser igual a <c>0.1.2</c>.
    /// </para>
    /// </summary>
    public static Version Current { get; } = Normalize(
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0));

    /// <summary>
    /// Interpreta a etiqueta. Aceita com e sem o <c>v</c> inicial, e ignora o sufixo
    /// de pré-lançamento (<c>v0.2.0-rc1</c>) — que o GitHub já sinaliza em campo
    /// próprio, muito mais confiável do que adivinhar pelo texto.
    /// </summary>
    /// <param name="tag">A etiqueta como veio da API.</param>
    /// <param name="version">A versão com três componentes.</param>
    /// <returns><see langword="false"/> para etiqueta que não é versão.</returns>
    public static bool TryParse(string? tag, [NotNullWhen(true)] out Version? version)
    {
        version = null;

        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        ReadOnlySpan<char> span = tag.AsSpan().Trim();

        if (span.Length > 0 && (span[0] == 'v' || span[0] == 'V'))
        {
            span = span[1..];
        }

        // Corta o sufixo de pré-lançamento e o de metadados de compilação.
        int corte = span.IndexOfAny('-', '+');
        if (corte >= 0)
        {
            span = span[..corte];
        }

        if (span.IsEmpty)
        {
            return false;
        }

        // Version.TryParse aceita "1" sozinho como inválido e "1.2" como 1.2 — mas
        // também aceitaria coisas com sinal e espaço. A conferência explícita dos
        // componentes evita depender desses cantos.
        Span<Range> partes = stackalloc Range[5];
        int quantas = SplitPontos(span, partes);

        if (quantas is < 2 or > 4)
        {
            return false;
        }

        Span<int> numeros = stackalloc int[3];
        for (int i = 0; i < 3; i++)
        {
            if (i >= quantas)
            {
                numeros[i] = 0;
                continue;
            }

            ReadOnlySpan<char> parte = span[partes[i]];
            if (parte.IsEmpty
                || !int.TryParse(parte, NumberStyles.None, CultureInfo.InvariantCulture, out int n))
            {
                return false;
            }

            numeros[i] = n;
        }

        version = new Version(numeros[0], numeros[1], numeros[2]);
        return true;
    }

    /// <summary>
    /// Se vale a pena oferecer <paramref name="candidata"/> a quem está em
    /// <paramref name="atual"/>.
    /// <para>
    /// Estritamente maior. Igual não é atualização, e MENOR nunca é oferecido: um
    /// lançamento despublicado ou uma etiqueta errada não podem empurrar o usuário
    /// para trás.
    /// </para>
    /// </summary>
    public static bool Vale(Version candidata, Version atual)
    {
        ArgumentNullException.ThrowIfNull(candidata);
        ArgumentNullException.ThrowIfNull(atual);

        return Normalize(candidata) > Normalize(atual);
    }

    /// <summary>Reduz a três componentes, tratando ausente como zero.</summary>
    public static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return new Version(
            version.Major,
            version.Minor,
            version.Build < 0 ? 0 : version.Build);
    }

    private static int SplitPontos(ReadOnlySpan<char> texto, Span<Range> destino)
    {
        int quantas = 0;
        int inicio = 0;

        for (int i = 0; i <= texto.Length; i++)
        {
            if (i != texto.Length && texto[i] != '.')
            {
                continue;
            }

            if (quantas == destino.Length)
            {
                return destino.Length + 1; // acima do aceito; o chamador rejeita
            }

            destino[quantas++] = new Range(inicio, i);
            inicio = i + 1;
        }

        return quantas;
    }
}
