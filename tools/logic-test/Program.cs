using RnsCompanion.Models;
using RnsCompanion.Services;

var failed = 0;
void Check(string name, bool condition)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
    if (!condition) failed++;
}

const string valid = "steam://joinlobby/393380/123456789/76561198000000000";
Check("Squad join URL accepted", SteamJoinUrl.IsSafe(valid));
Check("live two-part Squad join URL accepted",
    SteamJoinUrl.IsSafe("steam://joinlobby/393380/109775244207089156"));
Check("other Steam app rejected", !SteamJoinUrl.IsSafe("steam://joinlobby/730/1/2"));
Check("other Steam command rejected", !SteamJoinUrl.IsSafe("steam://run/393380"));
Check("web/file URL rejected", !SteamJoinUrl.IsSafe("https://example.com/file.exe") &&
                                !SteamJoinUrl.IsSafe("file:///C:/Windows/notepad.exe"));

var now = DateTime.UtcNow;
var startup = new GameStartupGate();
Check("cold start launches game", startup.ShouldStart(false, now));
startup.RecordLaunch(now);
Check("Steam startup is not repeatedly launched", !startup.ShouldStart(false, now.AddSeconds(30)));
Check("failed game launch can retry", startup.ShouldStart(false, now.AddMinutes(5)));
Check("running game is not relaunched", !startup.ShouldStart(true, now.AddMinutes(10)));
Check("no readiness without a running process", !startup.CanJoin(null));
var processStart = new DateTime(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc);
const string menuLine = "[2026.09.24-03.00.35:581][  0]LogLoad: Took 0.249506 seconds to LoadMap(/Game/Maps/EntryMap)";
Check("real Squad menu completion recognized", SquadStartupLog.IsMenuLoaded(menuLine, processStart));
Check("previous session completion rejected", !SquadStartupLog.IsMenuLoaded(menuLine, processStart.AddMinutes(1)));
Check("loading start is not readiness", !SquadStartupLog.IsMenuLoaded(
    "[2026.09.24-03.00.35:288][  0]LogSquad: OnPreLoadMap : Loading Main Menu", processStart));
Check("other map completion rejected", !SquadStartupLog.IsMenuLoaded(
    menuLine.Replace("/Game/Maps/EntryMap", "/Game/Maps/Sumari"), processStart));
Check("invalid timestamp rejected", !SquadStartupLog.IsMenuLoaded(menuLine.Replace("2026.09.24", "bad-date!!"), processStart));
var logPath = Path.GetTempFileName();
try
{
    var log = new SquadStartupLog(logPath);
    Check("empty log waits", !log.IsReady(processStart));
    File.AppendAllText(logPath, menuLine[..50]);
    Check("partial write waits", !log.IsReady(processStart));
    File.AppendAllText(logPath, menuLine[50..] + "\r\n");
    Check("appended completion enables join", log.IsReady(processStart));
    File.AppendAllText(logPath, "[2026.09.24-03.02.35:000][  0]LogWindows: Error: Fatal error:\n");
    log.IsReady(processStart);
    Check("fatal error detected after menu became ready", log.HasFatalError);
    Check("exit resets readiness", !log.IsReady(null));
    Check("exit clears fatal error", !log.HasFatalError);
    Check("restart rejects stale file", !log.IsReady(processStart.AddMinutes(1)));
    File.WriteAllText(logPath, "new log\n");
    Check("truncated file resets offset", !log.IsReady(processStart.AddMinutes(1)));
    File.AppendAllText(logPath, menuLine.Replace("03.00.35", "03.02.35") + "\n");
    Check("new session completion recognized", log.IsReady(processStart.AddMinutes(1)));
    File.Delete(logPath);
    Check("missing log on next startup waits", !log.IsReady(processStart.AddMinutes(3)));
}
finally { File.Delete(logPath); }

const string offlineLine = "[2026.09.22-06.08.33:094][116]LogOnlineGame: Warning: USQGameInstance::HandleNetworkConnectionStatusChanged: NotConnected";
const string onlineLine = "[2026.09.22-06.29.15:497][328]LogOnlineGame: Warning: USQGameInstance::HandleNetworkConnectionStatusChanged: Connected";
var onlineProcessStart = new DateTime(2026, 9, 22, 6, 0, 0, DateTimeKind.Utc);
var onlineLogPath = Path.GetTempFileName();
try
{
    var log = new SquadStartupLog(onlineLogPath);
    File.WriteAllText(onlineLogPath, offlineLine + "\n");
    log.IsReady(onlineProcessStart);
    Check("real disconnected log event recognized", log.OnlineDisconnectedSinceUtc == onlineProcessStart.AddMinutes(8).AddSeconds(33).AddMilliseconds(94));
    File.AppendAllText(onlineLogPath, onlineLine + "\n");
    log.IsReady(onlineProcessStart);
    Check("connection recovery cancels offline evidence", log.OnlineDisconnectedSinceUtc is null);
    File.AppendAllText(onlineLogPath, offlineLine + "\n");
    log.IsReady(onlineProcessStart.AddHours(1));
    Check("old session disconnect ignored", log.OnlineDisconnectedSinceUtc is null);
    File.WriteAllText(onlineLogPath, offlineLine + "\n" + new string('x', 1024 * 1024) + "\n" + onlineLine + "\n");
    log = new SquadStartupLog(onlineLogPath);
    log.IsReady(onlineProcessStart);
    Check("unread log backlog cannot trigger offline restart", log.OnlineDisconnectedSinceUtc is null);
    log.IsReady(onlineProcessStart);
    Check("recovery later in backlog clears failure", log.OnlineDisconnectedSinceUtc is null);
    File.WriteAllText(onlineLogPath, offlineLine + "\n");
    log.IsReady(onlineProcessStart);
    Check("log rotation reads new connection state", log.OnlineDisconnectedSinceUtc is not null);
    log.IsReady(null);
    Check("process exit clears connection state", log.OnlineDisconnectedSinceUtc is null);
}
finally { File.Delete(onlineLogPath); }

if (args.Length == 1)
{
    var realLog = new SquadStartupLog(args[0]);
    var found = false;
    for (var i = 0; i < 128 && !found; i++) found = realLog.IsReady(processStart);
    Check("local Squad log replay confirms menu load", found);
    Check("local Squad log rejected for a future process", !realLog.IsReady(DateTime.UtcNow.AddDays(1)));
}
var response = new AutoseedMyResponse
{
    Enabled = true,
    Target = new TargetInfo { Key = "server-1" },
    JoinUrl = valid,
};
Check("first join starts", SeedDecisions.ShouldLaunchJoin(response, null, null, now));
Check("duplicate join throttled", !SeedDecisions.ShouldLaunchJoin(
    response, "server-1", now.AddSeconds(-30), now));
Check("join allowed after cooldown", SeedDecisions.ShouldLaunchJoin(
    response, "server-1", now.AddMinutes(-3), now));
response.OnTarget = true;
Check("confirmed presence stops retries", !SeedDecisions.ShouldLaunchJoin(
    response, "server-1", now.AddMinutes(-3), now));
response.OnTarget = false;
Check("join retries at exactly two minutes", SeedDecisions.ShouldLaunchJoin(
    response, "server-1", now.AddMinutes(-2), now));
response.JoinUrl = "https://example.com";
Check("unsafe API join blocked", !SeedDecisions.ShouldLaunchJoin(response, null, null, now));
response.JoinUrl = null;
Check("missing join URL does not block game startup or link lookup",
    SeedDecisions.ShouldAttemptJoin(response, null, null, now));
Check("missing join URL cannot be launched", !SeedDecisions.ShouldLaunchJoin(response, null, null, now));
Check("fallback respects cooldown", !SeedDecisions.ShouldAttemptJoin(response, "server-1", now, now));
response.OnTarget = true;
Check("fallback stops on confirmed presence", !SeedDecisions.ShouldAttemptJoin(response, null, null, now));
response.OnTarget = false;
response.Target = null;
Check("no target cannot start game", !SeedDecisions.ShouldAttemptJoin(response, null, null, now));

Check("explicit completion", SeedDecisions.IsSeedCompleted(false,
    new AutoseedMyResponse { Enabled = true, AllSeeded = true }));
Check("disabled mode not completed", !SeedDecisions.IsSeedCompleted(true,
    new AutoseedMyResponse { Enabled = false, AllSeeded = true }));

Check("missing target is not proof of completion", !SeedDecisions.IsSeedCompleted(true,
    new AutoseedMyResponse { Enabled = true, Target = null }));
var recovery = new GameRecoveryPolicy();
Check("slow frame does not restart", recovery.GetRestartReason(true, false, true, false, now, now) is null);
Check("sustained hang restarts", recovery.GetRestartReason(true, false, true, false, now, now.AddMinutes(2)) is not null);
recovery.RecordRestart(now.AddMinutes(2));
Check("restart cooldown limits failures", recovery.GetRestartReason(true, false, false, false, now, now.AddMinutes(3), true) is null);
Check("fatal crash restarts after cooldown", recovery.GetRestartReason(true, true, true, true, now, now.AddMinutes(7), true) is not null);
recovery = new GameRecoveryPolicy();
Check("normal startup allowed", recovery.GetRestartReason(true, true, false, false, now, now.AddMinutes(9)) is null);
Check("stuck startup recovered", recovery.GetRestartReason(true, true, false, false, now, now.AddMinutes(10)) is not null);
recovery = new GameRecoveryPolicy();
Check("temporary lobby failure allowed", recovery.GetRestartReason(true, true, true, false, now, now) is null);
Check("persistent lobby failure recycles game", recovery.GetRestartReason(true, true, true, false, now, now.AddMinutes(15)) is not null);
Check("confirmed return resets watchdog", recovery.GetRestartReason(true, true, true, true, now, now.AddMinutes(16)) is null);
Check("new disconnect gets fresh grace period", recovery.GetRestartReason(true, true, true, false, now, now.AddMinutes(17)) is null);
Check("exited process requires no kill", recovery.GetRestartReason(false, false, false, false, null, now.AddMinutes(40)) is null);
recovery = new GameRecoveryPolicy();
Check("brief online outage gets recovery grace", recovery.GetRestartReason(true, true, true, false, now, now.AddSeconds(119), onlineDisconnectedSinceUtc: now) is null);
Check("responsive game with persistent online failure restarts", recovery.GetRestartReason(true, true, true, false, now, now.AddMinutes(2), onlineDisconnectedSinceUtc: now) is not null);
Check("healthy confirmed player is not kicked for service warning", recovery.GetRestartReason(true, true, true, true, now, now.AddMinutes(3), onlineDisconnectedSinceUtc: now) is null);
Check("recovered connection does not force restart", recovery.GetRestartReason(true, true, true, false, now, now.AddMinutes(4)) is null);
Check("previous process offline state cannot restart new process", recovery.GetRestartReason(true, true, true, false, now.AddMinutes(5), now.AddMinutes(6), onlineDisconnectedSinceUtc: now) is null);
recovery.RecordRestart(now.AddMinutes(6));
Check("online outage obeys restart cooldown", recovery.GetRestartReason(true, true, true, false, now.AddMinutes(5), now.AddMinutes(8), onlineDisconnectedSinceUtc: now.AddMinutes(5)) is null);

Console.WriteLine(failed == 0 ? "ALL TESTS PASSED" : $"FAILURES: {failed}");
return failed == 0 ? 0 : 1;
