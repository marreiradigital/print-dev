using System.IO;
using System.Runtime.InteropServices;

namespace PrintDev.Core.Storage;

/// <summary>
/// Manda arquivos para a Lixeira em vez de apagá-los de vez.
/// <para>
/// Toda remoção de arquivo do usuário feita pelo Print Dev passa por aqui. A diferença
/// entre apagar e mandar para a Lixeira é a diferença entre um erro recuperável e um
/// arquivo perdido — e quem decide o que é lixo é o dono da máquina, não o programa.
/// </para>
/// </summary>
public static class RecycleBin
{
    private const uint FO_DELETE = 0x0003;

    /// <summary>Manda para a Lixeira em vez de apagar.</summary>
    private const ushort FOF_ALLOWUNDO = 0x0040;

    /// <summary>Sem caixa de diálogo, sem barra de progresso, sem confirmação.</summary>
    private const ushort FOF_NO_UI = 0x0004 | 0x0010 | 0x0400 | 0x0200;

    /// <summary>
    /// Tamanho que a estrutura tem de ter em 64 bits.
    /// <para>
    /// Existe para o teste conferir. Um erro de alinhamento aqui não dá exceção: dá
    /// <c>AccessViolationException</c>, que o .NET <b>não deixa capturar</b> — o processo
    /// morre inteiro, sem log e sem rastro. Foi exatamente o que aconteceu quando esta
    /// estrutura tinha <c>Pack = 1</c>, herdado de exemplo de 32 bits: os campos a partir
    /// do terceiro caíam 4 bytes antes do lugar, e o Windows lia o caminho de um endereço
    /// que não era nosso.
    /// </para>
    /// </summary>
    internal const int TamanhoEsperadoDaEstrutura = 56;

    /// <summary>
    /// <c>SHFILEOPSTRUCTW</c>, com o alinhamento natural do Win64.
    /// <para>
    /// Os caminhos entram como ponteiro cru, e não como <see langword="string"/>: a lista
    /// exige terminação nula <b>dupla</b>, e depender do empacotador para preservar nulos
    /// no meio de uma cadeia é apostar num detalhe não documentado.
    /// </para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr Window;
        public uint Function;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    /// <summary>Move um arquivo para a Lixeira.</summary>
    /// <returns>
    /// <see langword="true"/> quando o arquivo saiu do lugar — inclusive quando ele já
    /// não existia, porque o resultado desejado é o mesmo.
    /// </returns>
    public static bool Send(string path) => Send(path, out _);

    /// <summary>
    /// Move um arquivo para a Lixeira, dizendo o motivo quando não consegue.
    /// </summary>
    public static bool Send(string path, out string? motivo)
    {
        motivo = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            motivo = "caminho vazio";
            return false;
        }

        string completo;
        try
        {
            // A API do shell não entende caminho relativo nem o prefixo de caminho longo.
            completo = Path.GetFullPath(path);
        }
        catch (Exception erro)
        {
            motivo = erro.Message;
            return false;
        }

        if (!File.Exists(completo) && !Directory.Exists(completo))
        {
            return true;
        }

        // A lista de origem termina em nulo DUPLO: um encerra o caminho, outro encerra a
        // lista. O '\0' extra na cadeia vira o primeiro; o empacotador acrescenta o segundo.
        IntPtr origem = Marshal.StringToHGlobalUni(completo + '\0');

        try
        {
            var operacao = new SHFILEOPSTRUCT
            {
                Function = FO_DELETE,
                From = origem,
                Flags = FOF_ALLOWUNDO | FOF_NO_UI,
            };

            int codigo = SHFileOperation(ref operacao);

            if (operacao.AnyOperationsAborted)
            {
                motivo = "a operação foi interrompida";
                return false;
            }

            if (codigo != 0)
            {
                motivo = Explicar(codigo);
                return false;
            }

            // O shell responde sucesso antes de o arquivo sair do disco em alguns casos;
            // quem confere é o disco, não o código de retorno.
            return !File.Exists(completo) && !Directory.Exists(completo);
        }
        finally
        {
            Marshal.FreeHGlobal(origem);
        }
    }

    /// <summary>
    /// Traduz os códigos do shell, que são anteriores ao <c>GetLastError</c> e não
    /// aparecem em lugar nenhum do .NET.
    /// </summary>
    private static string Explicar(int codigo) => codigo switch
    {
        0x71 => "vários arquivos para um destino só",
        0x74 => "não dá para mandar a raiz de um disco para a Lixeira",
        0x75 => "cancelado pelo usuário",
        0x78 => "acesso negado ao arquivo",
        0x79 => "o caminho é longo demais para o shell",
        0x7C => "caminho inválido",
        0x402 => "o shell não encontrou o arquivo",
        0x10000 => "erro no destino",
        _ => $"o shell recusou com o código 0x{codigo:X}",
    };
}
