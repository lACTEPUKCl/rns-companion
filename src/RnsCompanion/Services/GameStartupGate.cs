namespace RnsCompanion.Services;

/// <summary>Waits for menu load evidence from the running game's log.</summary>
internal sealed class GameStartupGate
{
    public static readonly TimeSpan LaunchRetry = TimeSpan.FromMinutes(5);
    private readonly SquadStartupLog _log = new();
    private DateTime? _launchUtc;

    public bool CanJoin(DateTime? processStartUtc) => _log.IsReady(processStartUtc);
    public bool HasFatalError => _log.HasFatalError;
    public DateTime? OnlineDisconnectedSinceUtc => _log.OnlineDisconnectedSinceUtc;

    public bool ShouldStart(bool running, DateTime nowUtc) =>
        !running && (_launchUtc is null || nowUtc - _launchUtc.Value >= LaunchRetry);

    public void RecordLaunch(DateTime nowUtc) => _launchUtc = nowUtc;
}
