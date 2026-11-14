using RnsCompanion.Models;

namespace RnsCompanion.Services;

internal static class ScheduleLaunchPolicy
{
    public static bool IsDue(AppSettings settings, DateTime localNow)
    {
        if (!settings.ScheduleEnabled) return false;
        if (!settings.ScheduleEveryDay && !settings.ScheduleDays.Contains((int)localNow.DayOfWeek))
            return false;
        if (!TimeSpan.TryParse(settings.ScheduleTime, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            return false;
        var delay = localNow.TimeOfDay - time;
        return delay >= TimeSpan.Zero && delay <= TimeSpan.FromMinutes(10);
    }
}
