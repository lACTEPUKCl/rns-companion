using System.IO;

namespace RnsCompanion.Services;

/// <summary>
/// Локальный файловый лог: %LocalAppData%\RNS\Companion\logs\companion-yyyyMMdd.log.
/// Потокобезопасный, с трансляцией строк в UI-журнал.
/// </summary>
internal static class LogService
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RNS", "Companion");

    private static readonly string LogDir = Path.Combine(DataDir, "logs");
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static string _currentDate = "";

    /// <summary>Событие для журнала в UI (вызывается из произвольного потока).</summary>
    public static event Action<string>? LineWritten;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (Sync)
        {
            try
            {
                EnsureWriter();
                _writer?.WriteLine(line);
            }
            catch (IOException) { /* лог не должен ронять приложение */ }
            catch (UnauthorizedAccessException) { }
        }
        LineWritten?.Invoke(line);
    }

    private static void EnsureWriter()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        if (_writer is not null && _currentDate == today) return;

        _writer?.Dispose();
        Directory.CreateDirectory(LogDir);
        // FileShare.ReadWrite: второй экземпляр (проброс /scheduled, rnscompanion://)
        // тоже должен писать в лог — иначе он «немой», пока жив первый, и запуск
        // из планировщика в 06:00 не оставляет никаких следов для диагностики.
        _writer = new StreamWriter(
            new FileStream(Path.Combine(LogDir, $"companion-{today}.log"),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };
        _currentDate = today;
    }

    public static void Flush()
    {
        lock (Sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Последние строки сегодняшнего лога — для предзаполнения журнала в UI.</summary>
    public static IReadOnlyList<string> ReadRecentLines(int count)
    {
        try
        {
            var path = Path.Combine(LogDir, $"companion-{DateTime.Now:yyyyMMdd}.log");
            if (!File.Exists(path)) return Array.Empty<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
                lines.Add(line);
            return lines.Count <= count ? lines : lines.Skip(lines.Count - count).ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }
}
