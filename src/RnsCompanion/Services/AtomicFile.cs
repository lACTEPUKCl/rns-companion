using System.IO;

namespace RnsCompanion.Services;

/// <summary>Запись через файл в том же каталоге и атомарное переименование.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents) =>
        Write(path, tmp => File.WriteAllText(tmp, contents));

    public static void WriteAllBytes(string path, byte[] contents) =>
        Write(path, tmp => File.WriteAllBytes(tmp, contents));

    private static void Write(string path, Action<string> writeTemp)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            writeTemp(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tmp); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
