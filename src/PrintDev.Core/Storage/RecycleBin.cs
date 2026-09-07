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

    /// <summary>Sem caixa de diálogo nenhuma.</summary>
    private const ushort FOF_NO_UI = 0x0004 | 0x0010 | 0x0400 | 0x0200;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    /// <summary>
    /// Move um arquivo para a Lixeira.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> quando o arquivo saiu do lugar — inclusive quando ele já
    /// não existia, porque o resultado desejado é o mesmo.
    /// </returns>
    public static bool Send(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (!File.Exists(path))
        {
            return true;
        }

        // A API espera a lista de caminhos com terminacao nula DUPLA: um nulo encerra
        // cada caminho e outro encerra a lista.
        var operation = new SHFILEOPSTRUCT
        {
            Function = FO_DELETE,
            From = path + "\0\0",
            Flags = FOF_ALLOWUNDO | FOF_NO_UI,
        };

        try
        {
            return SHFileOperation(ref operation) == 0 && !operation.AnyOperationsAborted;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
