using System.Net;
using RnsCompanion.Models;

namespace RnsCompanion.Services;

// External boundaries only: the production SeedController loop runs unchanged.
internal sealed class ApiClient
{
    public Func<Task<AutoseedMyResponse?>> Read = () => Task.FromResult<AutoseedMyResponse?>(null);
    public Func<Task> Start = () => Task.CompletedTask;
    public Func<Task<AutoseedStatusResponse?>> Status = () => Task.FromResult<AutoseedStatusResponse?>(
        new() { Window = new() { Open = true } });
    public Task<AutoseedMyResponse?> GetMyAsync(CancellationToken ct) => Read();
    public Task StartSeedAsync(CancellationToken ct) => Start();
    public Task StopSeedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<AutoseedStatusResponse?> GetStatusAsync(CancellationToken ct) => Status();
    public Task<string?> GetJoinUrlAsync(string name, CancellationToken ct) => Task.FromResult<string?>(null);
}
internal sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode StatusCode => status;
    public bool IsAuthError => status == HttpStatusCode.Unauthorized;
}
internal sealed class SeedWindowClosedException : Exception { public DateTime? OpensAt => null; }
internal static class GameProcessService
{
    public const int SquadAppId = 393380;
    public static bool Running;
    public static int Starts, Closes;
    public static bool IsGameRunning() => Running;
    public static (bool Running, bool HasWindow, DateTime? StartedAtUtc, bool Responding) GetStartupState()
        => (Running, Running, Running ? DateTime.UtcNow.AddMinutes(-20) : null, true);
    public static void StartGame() { Starts++; Running = true; GameStartupGate.Fatal = false; GameStartupGate.OfflineSince = null; }
    public static Task CloseGameAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Closes++; Running = false;
        return Task.CompletedTask;
    }
    public static bool IsSafeJoinUrl(string? url) => SteamJoinUrl.IsSafe(url);
}
internal sealed class GameStartupGate
{
    public static bool Fatal;
    public bool HasFatalError => Fatal;
    public static DateTime? OfflineSince;
    public DateTime? OnlineDisconnectedSinceUtc => OfflineSince;
    public bool CanJoin(DateTime? start) => start is not null;
    public bool ShouldStart(bool running, DateTime now) => !running;
    public void RecordLaunch(DateTime now) { }
}
internal static class LogService
{
    public static string DataDir = Path.Combine(Path.GetTempPath(), "rns-recovery-test-" + Guid.NewGuid());
    public static void Info(string text) => Console.WriteLine(text);
    public static void Warn(string text) => Console.WriteLine(text);
    public static void Error(string text, Exception ex) => Console.WriteLine(text + ": " + ex.Message);
}
internal static class AtomicFile { public static void WriteAllBytes(string path, byte[] bytes) { } }
internal sealed class ConfigSwapService
{
    public static ConfigSwapService Instance = new();
    public void ApplyLowPreset() { }
    public void RestoreIfNeeded(string reason) { }
}
internal static class PowerService
{
    private sealed class Lease : IDisposable { public void Dispose() { } }
    public static IDisposable KeepSystemAwake(string reason) => new Lease();
    public static void MonitorsOff() => throw new Exception("Unexpected monitor action");
    public static void Sleep() => throw new Exception("Unexpected sleep action");
}
