using RnsCompanion.Models;

namespace RnsCompanion.Services;

internal static class ScheduleLaunchPolicy
{
    public static bool TryGetTime(string? value, out TimeSpan time) =>
        TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out time) &&
        time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) && time.Ticks % TimeSpan.TicksPerMinute == 0;

    public static bool IsDue(AppSettings settings, DateTime localNow)
    {
        if (!settings.ScheduleEnabled) return false;
        if (!settings.ScheduleEveryDay && !settings.ScheduleDays.Contains((int)localNow.DayOfWeek))
            return false;
        if (!TryGetTime(settings.ScheduleTime, out var time))
            return false;
        var delay = localNow.TimeOfDay - time;
        return delay >= TimeSpan.Zero && delay <= TimeSpan.FromMinutes(10);
    }
}
