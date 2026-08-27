using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace RnsCompanion.Services;

/// <summary>
/// Задача Windows Task Scheduler без NuGet-зависимостей:
///   • создание/удаление — schtasks.exe (per-user, без админки);
///   • WakeToRun и прочие настройки, недоступные schtasks — PowerShell
///     (Get/Set-ScheduledTask), как в проверенном nsd-autostart.bat.
/// Ежедневный запуск — /SC DAILY; по дням недели — /SC WEEKLY /D MON,TUE,…
/// </summary>
internal static class SchedulerService
{
    public const string TaskName = "RNS Companion";

    /// <summary>Страховочная задача: при входе пользователя в систему восстанавливает
    /// GameUserSettings.ini, если приложение было убито во время подмены.</summary>
    public const string RestoreGuardTaskName = "RNS Companion RestoreGuard";

    private static readonly string[] DayCodes = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    /// <summary>Дни недели → битовая маска (Вс=1, Пн=2, … Сб=64).</summary>
    public static int DaysOfWeekMask(IEnumerable<DayOfWeek> days) =>
        days.Distinct().Aggregate(0, (mask, d) => mask | (1 << (int)d));

    public static bool TaskExists()
    {
        var (exitCode, _, _) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\"", timeoutSec: 15);
        return exitCode == 0;
    }

    /// <summary>Человекочитаемое описание текущей задачи в планировщике (для окна настроек).</summary>
    public static string? GetTaskSummary()
    {
        var ps =
            "$t = Get-ScheduledTask -TaskName '" + TaskName + "' -ErrorAction SilentlyContinue; " +
            "if ($null -eq $t) { exit 3 }; " +
            "$i = Get-ScheduledTaskInfo -TaskName '" + TaskName + "' -ErrorAction SilentlyContinue; " +
            "$tr = $t.Triggers | Select-Object -First 1; " +
            "[pscustomobject]@{ State = [string]$t.State; WakeToRun = [bool]$t.Settings.WakeToRun; " +
            "TriggerType = $tr.CimClass.CimClassName; DaysInterval = [int]$tr.DaysInterval; " +
            "StartBoundary = [string]$tr.StartBoundary; LastRunTime = [string]$i.LastRunTime; " +
            "NextRunTime = [string]$i.NextRunTime; LastTaskResult = [int]$i.LastTaskResult } | ConvertTo-Json -Compress";
        var (exitCode, stdout, _) = RunPowerShell(ps, timeoutSec: 30);
        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout)) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var state = root.GetProperty("State").GetString() ?? "?";
            var wake = root.GetProperty("WakeToRun").GetBoolean();
            var type = root.GetProperty("TriggerType").GetString() ?? "";
            var start = root.GetProperty("StartBoundary").GetString() ?? "";
            var time = DateTime.TryParse(start, out var dt) ? dt.ToString("HH:mm") : start;
            var kind = type.Contains("Daily")
                ? $"ежедневно (интервал {root.GetProperty("DaysInterval").GetInt32()} дн.)"
                : type.Contains("Weekly") ? "по дням недели" : "триггер другого типа";
            var lastResult = root.TryGetProperty("LastTaskResult", out var result)
                ? result.GetInt32() : 0;
            var nextText = root.TryGetProperty("NextRunTime", out var next) &&
                           DateTime.TryParse(next.GetString(), out var nextRun)
                ? nextRun.ToString("dd.MM HH:mm") : "—";
            return $"{kind} в {time}, WakeToRun={(wake ? "да" : "нет")}, состояние: {state}, " +
                   $"следующий: {nextText}, прошлый результат: 0x{lastResult:X8}";
        }
        catch (JsonException) { return "задача существует"; }
        catch (KeyNotFoundException) { return "задача существует"; }
    }

    /// <summary>
    /// Создаёт/обновляет задачу запуска приложения с аргументом /scheduled.
    /// Все 7 дней → DAILY, иначе WEEKLY с перечнем дней.
    /// </summary>
    public static void Register(IReadOnlyCollection<DayOfWeek> days, TimeSpan time, bool wakeToRun)
    {
        if (days.Count == 0)
            throw new InvalidOperationException("Не выбран ни один день недели.");

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь приложения.");

        var everyDay = DaysOfWeekMask(days) == 0x7F;
        var timeText = time.ToString(@"hh\:mm");
        var scheduleArgs = everyDay
            ? "/SC DAILY"
            : "/SC WEEKLY /D " + string.Join(",", days.Distinct().OrderBy(d => d).Select(d => DayCodes[(int)d]));

        var (exitCode, _, stderr) = Run("schtasks.exe",
            $"/Create /F /TN \"{TaskName}\" {scheduleArgs} /ST {timeText} " +
            $"/TR \"\\\"{exePath}\\\" /scheduled\"",
            timeoutSec: 30);
        if (exitCode != 0)
            throw new InvalidOperationException($"schtasks /Create завершился с кодом {exitCode}: {stderr.Trim()}");

        // WakeToRun и соседние настройки schtasks не умеет — докручиваем через PowerShell.
        var ps =
            "$t = Get-ScheduledTask -TaskName '" + TaskName + "' -ErrorAction Stop; " +
            "$s = $t.Settings; " +
            "$s.WakeToRun = $" + (wakeToRun ? "true" : "false") + "; " +
            "$s.StartWhenAvailable = $true; " +
            "$s.DisallowStartIfOnBatteries = $false; " +
            "$s.StopIfGoingOnBatteries = $false; " +
            "$s.MultipleInstances = 'IgnoreNew'; " +
            "$s.RestartCount = 3; " +
            "$s.RestartInterval = 'PT1M'; " +
            "$s.ExecutionTimeLimit = 'PT0S'; " +
            "Set-ScheduledTask -TaskName '" + TaskName + "' -Settings $s -ErrorAction Stop | Out-Null";
        var (psExit, _, psErr) = RunPowerShell(ps, timeoutSec: 60);
        if (psExit != 0)
            throw new InvalidOperationException(
                $"Задача создана, но WakeToRun и настройки восстановления запуска не применены: {psErr.Trim()}");

        LogService.Info($"Планировщик: задача «{TaskName}» сохранена " +
                        $"({(everyDay ? "ежедневно" : "дни: " + string.Join(",", days))}, " +
                        $"{timeText}, WakeToRun={wakeToRun}).");
    }

    public static void Delete()
    {
        var (exitCode, _, _) = Run("schtasks.exe", $"/Delete /F /TN \"{TaskName}\"", timeoutSec: 15);
        if (exitCode == 0)
            LogService.Info($"Планировщик: задача «{TaskName}» удалена.");
    }

    // ─────────────────── RestoreGuard (вход в систему) ───────────────────

    /// <summary>
    /// Регистрирует/обновляет задачу «RNS Companion RestoreGuard» с logon-триггером:
    /// при каждом входе в систему запускается `exe /restore-if-swapped` — мгновенный
    /// выход, если подмены нет; восстановление конфига, если приложение убили во время подмены.
    /// </summary>
    public static void RegisterRestoreGuard()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь приложения.");

        // schtasks не умеет per-user ONLOGON без админки («Отказано в доступе») —
        // регистрируем через ScheduledTasks-cmdlet'ы с interactive token (без админки).
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var ps =
            "$a = New-ScheduledTaskAction -Execute '" + PsQuote(exePath) + "' -Argument '/restore-if-swapped'; " +
            "$t = New-ScheduledTaskTrigger -AtLogOn -User '" + PsQuote(user) + "'; " +
            "$p = New-ScheduledTaskPrincipal -UserId '" + PsQuote(user) + "' -LogonType Interactive; " +
            "Register-ScheduledTask -TaskName '" + RestoreGuardTaskName + "' -Action $a -Trigger $t " +
            "-Principal $p -Force -ErrorAction Stop | Out-Null";
        var (exitCode, stdout, stderr) = RunPowerShell(ps, timeoutSec: 60);
        if (exitCode != 0)
        {
            LogService.Error($"Планировщик: RestoreGuard не создан (код {exitCode}): {(stdout + stderr).Trim()}");
            throw new InvalidOperationException(
                $"Не удалось создать задачу «{RestoreGuardTaskName}» (код {exitCode}): {(stdout + stderr).Trim()}");
        }
        LogService.Info($"Планировщик: задача «{RestoreGuardTaskName}» зарегистрирована (при входе в систему).");
    }

    public static void DeleteRestoreGuard()
    {
        var (exitCode, _, _) = Run("schtasks.exe", $"/Delete /F /TN \"{RestoreGuardTaskName}\"", timeoutSec: 15);
        if (exitCode == 0)
            LogService.Info($"Планировщик: задача «{RestoreGuardTaskName}» удалена.");
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(
        string fileName, string arguments, int timeoutSec)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // schtasks и PowerShell пишут в консольную кодировку OEM (866 на ru-RU) —
            // без этого русские ошибки читаются кракозябрами.
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Не удалось запустить {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutSec * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException($"{fileName} не ответил за {timeoutSec} с.");
        }
        Task.WhenAll(stdoutTask, stderrTask).GetAwaiter().GetResult();
        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    private static Encoding OemEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { return Encoding.GetEncoding(866); }
            catch (ArgumentException) { return Encoding.UTF8; }
        }
    }

    private static string PsQuote(string value) => value.Replace("'", "''");

    private static (int ExitCode, string StdOut, string StdErr) RunPowerShell(string command, int timeoutSec)
    {
        // -EncodedCommand принимает UTF-16LE: кириллица и кавычки в пути не проходят
        // через OEM-кодировку/парсер командной строки.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return Run("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
            timeoutSec);
    }
}
