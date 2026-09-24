using System.Diagnostics;

namespace RnsCompanion.Services;

/// <summary>Контроль процесса игры Squad (appid 393380).</summary>
internal static class GameProcessService
{
    public const int SquadAppId = SteamJoinUrl.SquadAppId;

    private static readonly string[] ProcessNames =
    {
        "SquadGame-Win64-Shipping",
        "SquadGame",
    };

    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(15);

    public static bool IsGameRunning() => GetStartupState().Running;

    public static (bool Running, bool HasWindow, DateTime? StartedAtUtc, bool Responding) GetStartupState()
    {
        var processes = GetGameProcesses();
        var running = false;
        var hasWindow = false;
        var responding = true;
        DateTime? startedAtUtc = null;
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (process.HasExited) continue;
                    running = true;
                    hasWindow |= process.MainWindowHandle != IntPtr.Zero;
                    responding &= process.Responding;
                    var start = process.StartTime.ToUniversalTime();
                    if (startedAtUtc is null || start > startedAtUtc) startedAtUtc = start;
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        return (running, hasWindow, startedAtUtc, responding);
    }

    public static void StartGame() =>
        Process.Start(new ProcessStartInfo($"steam://run/{SquadAppId}") { UseShellExecute = true });

    public static async Task WaitForMenuAsync(CancellationToken ct)
    {
        var startup = new GameStartupGate();
        var deadline = DateTime.UtcNow.AddMinutes(10);
        ct.ThrowIfCancellationRequested();
        if (!IsGameRunning()) StartGame();
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (startup.CanJoin(GetStartupState().StartedAtUtc)) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        throw new TimeoutException("За 10 минут лог Squad не подтвердил загрузку главного меню. Проверьте игру и SquadGame.log.");
    }

    /// <summary>Разрешаем запускать только Steam lobby-ссылки именно для Squad.</summary>
    public static bool IsSafeJoinUrl(string? value) => SteamJoinUrl.IsSafe(value);

    /// <summary>Закрыть игру: сначала вежливо (CloseMainWindow), по таймауту — Kill.</summary>
    public static async Task CloseGameAsync(CancellationToken ct = default)
    {
        var processes = GetGameProcesses();
        if (processes.Count == 0) return;

        LogService.Info($"Закрываю игру ({processes.Count} процессов)…");
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    if (process.CloseMainWindow() &&
                        await WaitForExitAsync(process, GracefulTimeout, ct))
                        continue;
                    ct.ThrowIfCancellationRequested();
                    process.Kill(entireProcessTree: true);
                    await WaitForExitAsync(process, TimeSpan.FromSeconds(5), ct);
                }
                catch (InvalidOperationException) { /* процесс уже завершился */ }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    LogService.Warn($"Не удалось закрыть процесс игры: {ex.Message}");
                }
            }
        }
        LogService.Info("Игра закрыта.");
    }

    private static List<Process> GetGameProcesses() =>
        ProcessNames
            .SelectMany(Process.GetProcessesByName)
            .GroupBy(p => p.Id)
            .Select(g => g.First())
            .ToList();

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}
