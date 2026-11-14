using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

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
    private DateTime? _lastServerJoinUtc;
    private bool _serverWelcomed;
    private DateTime? _connectionPendingUtc;
    private string? _pendingEndpoint;
    private string? _connectedEndpoint;
    public bool IsCaughtUp => _caughtUp;
    public bool HasUnreadData { get; private set; }
    public DateTime? ConnectionPendingUtc => _caughtUp ? _connectionPendingUtc : null;
    public string? ConnectedEndpoint => _caughtUp ? _connectedEndpoint : null;
    public DateTime? LastServerJoinUtc => _caughtUp ? _lastServerJoinUtc : null;
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
                var payload = LogPayload(line);
                if (IsMenuLoaded(line, processStartUtc.Value))
                    _ready = true;
                if (IsCurrentLine(line, processStartUtc.Value) &&
                    (payload.StartsWith("LogWindows: Error:", StringComparison.Ordinal) ||
                     payload.StartsWith("LogCore: Error:", StringComparison.Ordinal) ||
                     payload.StartsWith("LogOutputDevice: Error:", StringComparison.Ordinal)) &&
                    (payload.Contains("Fatal error:", StringComparison.OrdinalIgnoreCase) ||
                     payload.Contains("Unhandled Exception:", StringComparison.OrdinalIgnoreCase)))
                    HasFatalError = true;
                if (IsCurrentLine(line, processStartUtc.Value))
                {
                    var eventTime = DateTime.ParseExact(line.Substring(1, 23),
                        "yyyy.MM.dd-HH.mm.ss:fff", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                    var networkFailed = (payload.StartsWith("LogNet:", StringComparison.Ordinal) &&
                        (payload.Contains("Network Failure:", StringComparison.Ordinal) ||
                         payload.Contains("Connection lost", StringComparison.Ordinal))) ||
                        payload.StartsWith("LogSquad: [USQGameInstance::OnClientConnectionRejected]", StringComparison.Ordinal) ||
                        payload.StartsWith("LogSquad: [USQGameInstance::HandleNetworkFailure]", StringComparison.Ordinal);
                    var queueClosed = _connectionPendingUtc is not null &&
                        payload.StartsWith("LogUIActionRouter:", StringComparison.Ordinal) &&
                        payload.Contains("Applying input config for leaf-most node [WBP_", StringComparison.Ordinal) &&
                        (payload.Contains("WBP_ServerBrowser_C_", StringComparison.Ordinal) ||
                         payload.Contains("WBP_MainMenu_C_", StringComparison.Ordinal) ||
                         payload.Contains("WBP_ServersScreen_C_", StringComparison.Ordinal) ||
                         payload.Contains("WBP_MainMenuScreen_C_", StringComparison.Ordinal) ||
                         payload.Contains("WBP_HomeScreen_C_", StringComparison.Ordinal));
                    var returnedToMenu = payload.StartsWith("LogNet: Browse: /Game/Maps/EntryMap", StringComparison.Ordinal) || queueClosed;
                    if (networkFailed || returnedToMenu)
                    {
                        _lastServerJoinUtc = null;
                        _serverWelcomed = false;
                        _connectionPendingUtc = null;
                        _pendingEndpoint = null;
                        _connectedEndpoint = null;
                    }
                    if ((payload.StartsWith("LogUIActionRouter:", StringComparison.Ordinal) &&
                         payload.Contains("Applying input config for leaf-most node [WBP_QueueInfo_C_", StringComparison.Ordinal)) ||
                        payload.StartsWith("LogSquad: [USQGameInstance::OnClientConnectionAccepted]", StringComparison.Ordinal) ||
                        payload.StartsWith("LogSquad: [USQGameInstance::JoinSession]: Joining the session", StringComparison.Ordinal))
                        _connectionPendingUtc ??= eventTime;
                    if (payload.StartsWith("LogNet: Browse:", StringComparison.Ordinal))
                    {
                        var remote = Regex.Match(payload, @"^LogNet: Browse: (\d{1,3}(?:\.\d{1,3}){3}:\d{1,5})/");
                        if (remote.Success)
                        {
                            _pendingEndpoint = remote.Groups[1].Value;
                            _connectionPendingUtc ??= eventTime;
                            _serverWelcomed = false;
                            _lastServerJoinUtc = null;
                            _connectedEndpoint = null;
                        }
                    }
                    if (payload.StartsWith("LogNet: Welcomed by server (Level:", StringComparison.Ordinal))
                        _serverWelcomed = true;
                    if (_serverWelcomed && _pendingEndpoint is not null &&
                        payload.StartsWith("LogLoad: Took ", StringComparison.Ordinal) &&
                        line.Contains(" seconds to LoadMap(", StringComparison.Ordinal) &&
                        !line.Contains("/Game/Maps/EntryMap)", StringComparison.Ordinal))
                    {
                        _lastServerJoinUtc = eventTime;
                        _connectedEndpoint = _pendingEndpoint;
                        _connectionPendingUtc = null;
                    }
                }
                const string connectionMarker = "LogOnlineGame: Warning: USQGameInstance::HandleNetworkConnectionStatusChanged: ";
                if (IsCurrentLine(line, processStartUtc.Value) && payload.StartsWith(connectionMarker, StringComparison.Ordinal))
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
            HasUnreadData = _offset < stream.Length;
            _caughtUp = !HasUnreadData;
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
        HasUnreadData = false;
        _lastServerJoinUtc = null;
        _serverWelcomed = false;
        _connectionPendingUtc = null;
        _pendingEndpoint = null;
        _connectedEndpoint = null;
        _fileCreated = default;
    }

    internal static bool IsMenuLoaded(string line, DateTime processStartUtc)
        => IsCurrentLine(line, processStartUtc) &&
           LogPayload(line).StartsWith("LogLoad: Took ", StringComparison.Ordinal) &&
           line.EndsWith(" seconds to LoadMap(/Game/Maps/EntryMap)", StringComparison.Ordinal);

    private static string LogPayload(string line)
    {
        if (line.Length < 26) return "";
        var frameEnd = line.IndexOf(']', 25);
        return frameEnd < 0 ? "" : line[(frameEnd + 1)..];
    }

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
