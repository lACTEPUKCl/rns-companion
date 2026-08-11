// swap-test — живой прогон защитных сценариев ConfigSwapService на ФЕЙКОВОМ INI.
// Реальный %LOCALAPPDATA%\SquadGame не трогаем: iniPathOverride ведёт в temp-папку,
// проверка «игра запущена» — через инжектируемый probe (на машине может быть запущена
// НАСТОЯЩАЯ игра — её нельзя трогать). Маркер/второй бэкап — в штатном
// %LocalAppData%\RNS\Companion (нужно для e2e с режимами /watchdog и
// /restore-if-swapped реального exe); дочерним процессам ставим
// RNS_COMPANION_GAME_RUNNING=0, чтобы они не ждали выхода настоящей игры.
//
// Использование: dotnet run -c Release -- "C:\...\publish\RNS.Companion.exe"

using System.Diagnostics;
using RnsCompanion.Services;

var failures = 0;
void Check(string name, bool cond)
{
    Console.WriteLine((cond ? "PASS" : "FAIL") + "  " + name);
    if (!cond) failures++;
}

var appExe = args.Length > 0 ? args[0] : null;

var tempDir = Path.Combine(Path.GetTempPath(), "rns-swap-test-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tempDir);
var fakeIni = Path.Combine(tempDir, "GameUserSettings.ini");
var primaryBak = Path.Combine(tempDir, "GameUserSettings.rnsbak.ini");
const string originalContent = "; оригинальный конфиг пользователя\nResolutionSizeX=2560\n";

// «Игра» по мнению харнеса — живые фейковые процессы (переименованный ping).
var fakeGames = new List<Process>();
bool FakeGameAlive()
{
    fakeGames.RemoveAll(p => p.HasExited);
    return fakeGames.Count > 0;
}

var svc = new ConfigSwapService(iniPathOverride: fakeIni, isGameRunning: FakeGameAlive);
var markerPath = Path.Combine(LogService.DataDir, "swap-state.json");
var secondBak = Path.Combine(LogService.DataDir, "backup", "GameUserSettings.ini");

var allFake = new List<Process>();
Process StartFake(string exeName, string pingArgs)
{
    var dst = Path.Combine(tempDir, exeName);
    File.Copy(@"C:\Windows\System32\ping.exe", dst, overwrite: true);
    var p = Process.Start(new ProcessStartInfo
    {
        FileName = dst,
        Arguments = pingArgs,
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;
    allFake.Add(p);
    return p;
}

Process StartFakeGame() { var p = StartFake("rns-fake-game.exe", "-n 200 127.0.0.1"); fakeGames.Add(p); return p; }

Process StartApp(string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = appExe!,
        Arguments = arguments,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    // Не ждём выхода настоящей игры (она может быть запущена на машине).
    psi.Environment["RNS_COMPANION_GAME_RUNNING"] = "0";
    return Process.Start(psi)!;
}

bool WaitForRestored(int seconds)
{
    for (var i = 0; i < seconds; i++)
    {
        Thread.Sleep(1000);
        if (!svc.IsSwapActive && File.ReadAllText(fakeIni) == originalContent)
            return true;
    }
    return false;
}

void CleanupSwap()
{
    foreach (var p in new[] { markerPath, secondBak, primaryBak,
             Path.Combine(LogService.DataDir, "backup", "backup.sha256") })
        try { File.Delete(p); } catch { }
}

try
{
    // ── S1: подмена → восстановление (roundtrip) ──
    File.WriteAllText(fakeIni, originalContent);
    Check("S1 apply: пресет подложен", svc.ApplyLowPreset(spawnWatchdog: false, registerGuard: false)
        && File.ReadAllText(fakeIni).Contains("[/Script/Squad.SQGameUserSettings]")
        && svc.IsSwapActive);
    Check("S1 apply: оба бэкапа + хэш созданы",
        File.Exists(primaryBak) && File.Exists(secondBak)
        && File.Exists(Path.Combine(LogService.DataDir, "backup", "backup.sha256")));
    svc.RestoreIfNeeded("S1");
    Check("S1 restore: оригинал возвращён, маркер снят",
        File.ReadAllText(fakeIni) == originalContent && !svc.IsSwapActive
        && !File.Exists(primaryBak) && !File.Exists(secondBak));

    // ── S2: основной бэкап битый → восстановление из второй копии ──
    svc.ApplyLowPreset(spawnWatchdog: false, registerGuard: false);
    File.WriteAllText(primaryBak, "GARBAGE-BROKEN-BACKUP");
    svc.RestoreIfNeeded("S2");
    Check("S2 restore: восстановлено из второй копии при битом rnsbak",
        File.ReadAllText(fakeIni) == originalContent && !svc.IsSwapActive);

    // ── S3: оба бэкапа битые → файл НЕ тронут, маркер оставлен ──
    svc.ApplyLowPreset(spawnWatchdog: false, registerGuard: false);
    File.WriteAllText(primaryBak, "GARBAGE-1");
    File.WriteAllText(secondBak, "GARBAGE-2");
    var swappedContent = File.ReadAllText(fakeIni);
    svc.RestoreIfNeeded("S3");
    Check("S3 restore: оба бэкапа битые — файл не изменён, маркер оставлен",
        File.ReadAllText(fakeIni) == swappedContent && svc.IsSwapActive);
    CleanupSwap(); // вручную чистим «аварийное» состояние
    File.WriteAllText(fakeIni, originalContent);

    // ── S4: игра запущена → подмена запрещена ──
    StartFakeGame();
    Thread.Sleep(1000);
    var appliedWhileRunning = svc.ApplyLowPreset(spawnWatchdog: false, registerGuard: false);
    Check("S4 guard: подмена при запущенной игре отклонена",
        !appliedWhileRunning && File.ReadAllText(fakeIni) == originalContent && !svc.IsSwapActive);

    // ── S5: exit-watcher — restore после выхода игры ──
    foreach (var g in fakeGames.ToArray()) { g.Kill(); g.WaitForExit(); }
    Thread.Sleep(500);
    Check("S5 apply после выхода игры", svc.ApplyLowPreset(spawnWatchdog: false, registerGuard: false));
    var game2 = StartFakeGame(); // игра «запустилась» с подменённым конфигом
    Thread.Sleep(4500);          // watcher фаза 1: увидеть игру (poll 3с)
    game2.Kill();                // игра «вышла»
    game2.WaitForExit();
    Check("S5 exit-watcher: конфиг восстановлен после полного выхода игры", WaitForRestored(25));

    if (appExe is not null && File.Exists(appExe))
    {
        // ── S6: watchdog — родителя убили (taskkill /F) во время подмены ──
        Check("S6 apply", svc.ApplyLowPreset(spawnWatchdog: false, registerGuard: false));
        var fakeParent = StartFake("rns-fake-parent.exe", "-n 300 127.0.0.1");
        var watchdog = StartApp($"/watchdog {fakeParent.Id}");
        Thread.Sleep(4000); // watchdog поднялся (single-file exe распаковывается)
        fakeParent.Kill();  // «убили приложение через диспетчер задач»
        fakeParent.WaitForExit();
        Check("S6 watchdog: конфиг восстановлен после убийства родителя", WaitForRestored(30));
        watchdog.WaitForExit(15000);
        Check("S6 watchdog: процесс-сторож завершился", watchdog.HasExited);

        // ── S7: /restore-if-swapped (RestoreGuard при входе в систему) ──
        Check("S7 apply", svc.ApplyLowPreset(spawnWatchdog: false, registerGuard: false));
        var guard = StartApp("/restore-if-swapped");
        guard.WaitForExit(30000);
        Check("S7 restore-guard: конфиг восстановлен, процесс вышел",
            guard.HasExited && !svc.IsSwapActive && File.ReadAllText(fakeIni) == originalContent);
    }
    else
    {
        Console.WriteLine("SKIP  S6/S7: путь к RNS.Companion.exe не передан");
    }
}
finally
{
    foreach (var p in allFake)
    {
        try { if (!p.HasExited) p.Kill(); } catch { }
        p.Dispose();
    }
    CleanupSwap();
    try { File.Delete(Path.Combine(LogService.DataDir, "watchdog.pid")); } catch { }
    try { Directory.Delete(tempDir, recursive: true); } catch { }
    SchedulerService.DeleteRestoreGuard(); // на случай, если тесты её создали
}

Console.WriteLine(failures == 0 ? "ALL TESTS PASSED" : $"FAILURES: {failures}");
return failures == 0 ? 0 : 1;
