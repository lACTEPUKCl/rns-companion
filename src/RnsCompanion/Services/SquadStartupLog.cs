using System.Globalization;
using System.IO;
using System.Text;

namespace RnsCompanion.Services;

/// <summary>Reads only new log bytes and accepts menu completion from the current process.</summary>
internal sealed class SquadStartupLog
{
    public static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SquadGame", "Saved", "Logs", "SquadGame.log");
    private readonly string _path;
    private DateTime? _processStart;
    private DateTime _fileCreated;
    private long _offset;
    private string _partial = "";
    private bool _ready;
    public bool HasFatalError { get; private set; }
    private DateTime? _onlineDisconnectedSinceUtc;
    private bool _caughtUp;
    // Do not act on an old disconnect while its recovery may still be unread.
    public DateTime? OnlineDisconnectedSinceUtc => _caughtUp ? _onlineDisconnectedSinceUtc : null;

    public SquadStartupLog(string? path = null) => _path = path ?? DefaultPath;

    public bool IsReady(DateTime? processStartUtc)
    {
        if (_processStart != processStartUtc)
        {
            _processStart = processStartUtc;
            ResetFile();
        }
        if (processStartUtc is null) return false;
        _caughtUp = false;
        try
        {
            var created = File.GetCreationTimeUtc(_path);
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (created != _fileCreated || stream.Length < _offset)
            {
                ResetFile();
                _fileCreated = created;
            }
            stream.Position = _offset;
            // Bound work per poll, even when resuming with a very large log.
            var buffer = new byte[(int)Math.Min(1024 * 1024, stream.Length - _offset)];
            var count = stream.Read(buffer, 0, buffer.Length);
            _offset += count;
            var text = _partial + Encoding.UTF8.GetString(buffer, 0, count);
            var start = 0;
            int end;
            while ((end = text.IndexOf('\n', start)) >= 0)
            {
                var line = text[start..end].TrimEnd('\r');
                if (IsMenuLoaded(line, processStartUtc.Value))
                    _ready = true;
                if (IsCurrentLine(line, processStartUtc.Value) &&
                    (line.Contains("Fatal error:", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("Unhandled Exception:", StringComparison.OrdinalIgnoreCase)))
                    HasFatalError = true;
                const string connectionMarker = "LogOnlineGame: Warning: USQGameInstance::HandleNetworkConnectionStatusChanged: ";
                if (IsCurrentLine(line, processStartUtc.Value) && line.Contains(connectionMarker, StringComparison.Ordinal))
                {
                    if (line.EndsWith(connectionMarker + "NotConnected", StringComparison.Ordinal))
                        _onlineDisconnectedSinceUtc ??= DateTime.ParseExact(line.Substring(1, 23),
                            "yyyy.MM.dd-HH.mm.ss:fff", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    else if (line.EndsWith(connectionMarker + "Connected", StringComparison.Ordinal))
                        _onlineDisconnectedSinceUtc = null;
                }
                start = end + 1;
            }
            _partial = text[start..];
            if (_partial.Length > 65536) _partial = "";
            _caughtUp = _offset == stream.Length && _partial.Length == 0;
            return _ready;
        }
        catch (IOException) { return _ready; }
        catch (UnauthorizedAccessException) { return _ready; }
    }

    private void ResetFile()
    {
        _offset = 0;
        _partial = "";
        _ready = false;
        HasFatalError = false;
        _onlineDisconnectedSinceUtc = null;
        _caughtUp = false;
        _fileCreated = default;
    }

    internal static bool IsMenuLoaded(string line, DateTime processStartUtc)
        => IsCurrentLine(line, processStartUtc) &&
           line.Contains("LogLoad: Took ", StringComparison.Ordinal) &&
           line.EndsWith(" seconds to LoadMap(/Game/Maps/EntryMap)", StringComparison.Ordinal);

    private static bool IsCurrentLine(string line, DateTime processStartUtc)
    {
        // Unreal timestamps are UTC. Never accept an old session's menu marker.
        if (line.Length < 25 || line[0] != '[' || line[24] != ']' ||
            !DateTime.TryParseExact(line.AsSpan(1, 23), "yyyy.MM.dd-HH.mm.ss:fff",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp) || timestamp < processStartUtc)
            return false;
        return true;
    }
}
