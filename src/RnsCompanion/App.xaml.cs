using System.Windows;
using RnsCompanion.Services;
using Application = System.Windows.Application;

namespace RnsCompanion;

public partial class App : Application
{
    public const string ProtocolScheme = "rnscompanion";
    private const string SingleInstanceName = "RNS.Companion.SingleInstance";

    /// <summary>Команда, которую второй экземпляр передаёт первому по named pipe,
    /// когда планировщик запустил приложение с /scheduled, а оно уже работает.</summary>
    internal const string ScheduledCommand = "/scheduled";

    private SingleInstanceService? _singleInstance;

    public static bool ScheduledLaunch { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── Headless-режимы (без окна, без single-instance) ──

        // Watchdog: следит за родителем и восстанавливает конфиг, если тот убит.
        if (e.Args is [var cmd, var pidArg, ..] &&
            cmd.Equals("/watchdog", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(pidArg, out var parentPid))
        {
            WatchdogService.Run(parentPid);
            LogService.Flush();
            Shutdown();
            return;
        }

        // RestoreGuard (задача планировщика «при входе в систему»):
        // если подмена активна — восстановить и выйти; иначе — мгновенный выход.
        if (e.Args.Any(a => a.Equals("/restore-if-swapped", StringComparison.OrdinalIgnoreCase)))
        {
            if (ConfigSwapService.Instance.IsSwapActive)
            {
                LogService.Info("RestoreGuard: обнаружена активная подмена при входе в систему.");
                ConfigSwapService.Instance.RestoreIfNeeded("вход в систему (RestoreGuard)");
            }
            LogService.Flush();
            Shutdown();
            return;
        }

        LogService.Info("Запуск приложения" + (e.Args.Length > 0 ? $" (аргументы: {string.Join(" ", e.Args)})" : ""));

        ScheduledLaunch = e.Args.Any(a =>
            string.Equals(a, ScheduledCommand, StringComparison.OrdinalIgnoreCase));

        _singleInstance = new SingleInstanceService(SingleInstanceName);
        var protocolUri = e.Args.FirstOrDefault(a =>
            a.StartsWith(ProtocolScheme + ":", StringComparison.OrdinalIgnoreCase));

        if (!_singleInstance.IsPrimary)
        {
            // Второй экземпляр (вызван браузером по rnscompanion:// или
            // планировщиком с /scheduled) — передаём команду первому и завершаемся.
            var message = protocolUri ?? (ScheduledLaunch ? ScheduledCommand : null);
            if (message is not null)
            {
                try { _singleInstance.Forward(message); }
                catch (Exception ex) { LogService.Warn($"Не удалось передать команду первому экземпляру: {ex.Message}"); }
            }
            Shutdown();
            return;
        }

        try { ProtocolRegistration.Register(ProtocolScheme); }
        catch (Exception ex) { LogService.Warn($"Регистрация протокола не удалась: {ex.Message}"); }

        // Crash-safe восстановление конфига игры, если прошлый запуск прервался во время подмены.
        try { ConfigSwapService.Instance.StartupRecover(); }
        catch (Exception ex) { LogService.Warn($"Восстановление GameUserSettings.ini не удалось: {ex.Message}"); }

        var window = new MainWindow(ScheduledLaunch);
        _singleInstance.MessageReceived += message =>
            window.Dispatcher.Invoke(() =>
            {
                if (message.Equals(ScheduledCommand, StringComparison.OrdinalIgnoreCase))
                    window.HandleScheduledCommand();
                else
                    window.HandleProtocolUri(message);
            });
        MainWindow = window;
        window.Show();

        if (protocolUri is not null)
            window.HandleProtocolUri(protocolUri);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Info("Завершение приложения");
        _singleInstance?.Dispose();
        LogService.Flush();
        base.OnExit(e);
    }
}
