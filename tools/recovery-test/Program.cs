using System.Net;
using RnsCompanion.Models;
using RnsCompanion.Services;

static void Check(string name, bool ok)
{
    if (!ok) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
static async Task Until(Func<bool> condition)
{
    var deadline = DateTime.UtcNow.AddSeconds(40);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException("Controller did not recover");
        await Task.Delay(20);
    }
}
static AutoseedMyResponse Presence(bool present = true) => new()
{
    Enabled = true, OnTarget = present, Target = new() { Key = "test", Name = "test" },
    Session = present ? new() { StartedAt = DateTime.UtcNow } : null
};
static SeedController Controller(ApiClient api) => new(api, () => new AppSettings
{
    CloseGameAfterSeed = false, MonitorOffInScheduledMode = false
});

// No real API, Steam, game process, user configuration, or power actions are used.
var api = new ApiClient();
var reads = 0;
api.Read = () => ++reads == 1
    ? Task.FromException<AutoseedMyResponse?>(new HttpRequestException("Simulated outage"))
    : Task.FromResult<AutoseedMyResponse?>(Presence());
GameProcessService.Running = true;
var seed = Controller(api);
try
{
    await seed.StartAsync(false, default);
    await Until(() => seed.State.StatusText.Contains("Сервис недоступен"));
    Check("network outage preserves enabled loop", seed.IsRunning);
    await Until(() => seed.State.Phase == SeedPhase.OnTarget);
    Check("actual polling resumes after API recovery", reads >= 2 && seed.IsRunning);
}
finally { await seed.StopAsync(); }

api = new ApiClient();
api.Read = () => Task.FromResult<AutoseedMyResponse?>(Presence());
GameStartupGate.Fatal = true;
seed = Controller(api);
try
{
    await seed.StartAsync(false, default);
    await Until(() => GameProcessService.Starts == 1);
    Check("fatal crash closes and relaunches Squad even with stale presence", GameProcessService.Closes == 1);
    await Until(() => seed.State.Phase == SeedPhase.OnTarget);
    Check("controller tracks returned player after restart", seed.IsRunning);
}
finally { await seed.StopAsync(); }

api = new ApiClient();
var starts = 0;
api.Start = () => ++starts == 1 ? Task.FromException(new ApiException(HttpStatusCode.ServiceUnavailable, "503")) : Task.CompletedTask;
api.Read = () => Task.FromResult<AutoseedMyResponse?>(Presence());
seed = Controller(api);
try
{
    await seed.StartAsync(false, default);
    await Until(() => seed.State.Phase == SeedPhase.OnTarget);
    Check("manual start survives initial service outage", starts == 2 && seed.IsRunning);
}
finally { await seed.StopAsync(); }

GameStartupGate.OfflineSince = DateTime.UtcNow.AddMinutes(-3);
api = new ApiClient { Read = () => Task.FromResult<AutoseedMyResponse?>(Presence(false)) };
seed = Controller(api);
try
{
    await seed.StartAsync(false, default);
    await Until(() => GameProcessService.Starts == 2);
    Check("responsive game with online connection loss is restarted", GameProcessService.Closes == 2 && seed.IsRunning);
}
finally { await seed.StopAsync(); }

GameProcessService.Running = false;
api = new ApiClient { Read = () => Task.FromResult<AutoseedMyResponse?>(Presence()) };
seed = Controller(api);
try
{
    await seed.StartAsync(false, default);
    await Until(() => GameProcessService.Starts == 3);
    Check("exited game relaunched despite stale server presence", seed.IsRunning);
}
finally { await seed.StopAsync(); }

api = new ApiClient { Read = () => Task.FromException<AutoseedMyResponse?>(
    new ApiException(HttpStatusCode.Unauthorized, "expired")) };
seed = Controller(api);
var authExpired = false;
seed.AuthExpired += () => authExpired = true;
await seed.StartAsync(false, default);
await Until(() => authExpired);
Check("expired credentials stop recovery and request login", !seed.IsRunning);

api = new ApiClient { Start = () => Task.FromException(new HttpRequestException("offline")),
    Status = () => Task.FromResult<AutoseedStatusResponse?>(new() { Window = new() { Open = false } }) };
seed = Controller(api);
await seed.StartAsync(false, default);
await seed.StopAsync();
await Task.Delay(100);
Check("Stop cancels pending recovery", !seed.IsRunning);
Console.WriteLine("ALL RECOVERY TESTS PASSED");
