using RnsCompanion.Models;

namespace RnsCompanion.Services;

/// <summary>Чистая логика решений цикла набора.</summary>
internal static class SeedDecisions
{
    public static readonly TimeSpan JoinMinInterval = TimeSpan.FromMinutes(2);

    public static bool ShouldLaunchJoin(
        AutoseedMyResponse my, string? lastJoinKey, DateTime? lastJoinUtc, DateTime nowUtc)
    {
        if (!my.Enabled || my.OnTarget) return false;
        if (my.Target?.Key is not { } key || !SteamJoinUrl.IsSafe(my.JoinUrl)) return false;
        if (lastJoinKey != key || lastJoinUtc is null) return true;
        return nowUtc - lastJoinUtc.Value >= JoinMinInterval;
    }

    public static bool IsSeedCompleted(bool targetWasSeen, AutoseedMyResponse my) =>
        my.Enabled && (my.AllSeeded ?? (targetWasSeen && my.Target is null));
}
