using System.Diagnostics;

namespace RnsCompanion.Services;

/// <summary>Контроль процесса игры Squad (appid 393380).</summary>
internal static class GameProcessService
{
    public const int SquadAppId = 393380;

    private static readonly string[] ProcessNames =
    {
        "SquadGame-Win64-Shipping",
        "SquadGame",
    };

    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(15);

    public static bool IsGameRunning() => GetGameProcesses().Count > 0;

    /// <summary>Закрыть игру: сначала вежливо (CloseMainWindow), по таймауту — Kill.</summary>
    public static async Task CloseGameAsync()
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
                    if (process.CloseMainWindow() &&
                        await WaitForExitAsync(process, GracefulTimeout))
                        continue;
                    process.Kill(entireProcessTree: true);
                    await WaitForExitAsync(process, TimeSpan.FromSeconds(5));
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

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }
}
