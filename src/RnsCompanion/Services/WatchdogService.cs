namespace RnsCompanion.Services;

/// <summary>
/// Watchdog: detached-режим того же exe (`/watchdog &lt;parentPid&gt;`), без окна.
/// Следит за родительским процессом: если тот умер (kill через диспетчер задач
/// и т.п.), а подмена GameUserSettings.ini всё ещё активна — ждёт выхода игры
/// (если она ещё идёт) и восстанавливает оригинал из бэкапа.
/// Если родитель жив и сам восстановил конфиг (маркер снят) — watchdog просто завершается.
/// </summary>
internal static class WatchdogService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan GameExitGrace = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    public static void Run(int parentPid)
    {
        LogService.Info($"Watchdog: старт (родитель PID {parentPid}).");
        ConfigSwapService.Instance.WriteWatchdogPid();

        var deadline = DateTime.UtcNow + MaxLifetime;
        while (DateTime.UtcNow < deadline)
        {
            if (!ConfigSwapService.Instance.IsSwapActive)
            {
                LogService.Info("Watchdog: маркер снят (родитель восстановил сам) — выход.");
                return;
            }

            if (!ConfigSwapService.ProcessAlive(parentPid))
            {
                LogService.Warn("Watchdog: родительский процесс исчез при активной подмене!");

                // Если игра ещё работает — ждём её выхода: она перезапишет INI при выходе,
                // восстанавливать нужно строго после.
                while (ConfigSwapService.Instance.GameIsRunning() && DateTime.UtcNow < deadline)
                    Thread.Sleep(PollInterval);

                Thread.Sleep(GameExitGrace); // игра дописывает INI при выходе
                ConfigSwapService.Instance.RestoreIfNeeded("watchdog: родитель умер");
                return;
            }

            Thread.Sleep(PollInterval);
        }

        LogService.Warn("Watchdog: превышено максимальное время жизни (24ч) — выход без действий.");
    }
}
