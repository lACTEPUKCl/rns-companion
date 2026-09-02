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
response.JoinUrl = "https://example.com";
Check("unsafe API join blocked", !SeedDecisions.ShouldLaunchJoin(response, null, null, now));

Check("explicit completion", SeedDecisions.IsSeedCompleted(false,
    new AutoseedMyResponse { Enabled = true, AllSeeded = true }));
Check("disabled mode not completed", !SeedDecisions.IsSeedCompleted(true,
    new AutoseedMyResponse { Enabled = false, AllSeeded = true }));

Console.WriteLine(failed == 0 ? "ALL TESTS PASSED" : $"FAILURES: {failed}");
return failed == 0 ? 0 : 1;
