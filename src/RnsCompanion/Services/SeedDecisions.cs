using RnsCompanion.Models;

namespace RnsCompanion.Services;

/// <summary>Чистая логика решений цикла набора.</summary>
internal static class SeedDecisions
{
    public static readonly TimeSpan JoinMinInterval = TimeSpan.FromMinutes(2);

    public static bool ShouldLaunchJoin(
        AutoseedMyResponse my, string? lastJoinKey, DateTime? lastJoinUtc, DateTime nowUtc)
        => SteamJoinUrl.IsSafe(my.JoinUrl) && ShouldAttemptJoin(my, lastJoinKey, lastJoinUtc, nowUtc);

    public static bool ShouldAttemptJoin(
        AutoseedMyResponse my, string? lastJoinKey, DateTime? lastJoinUtc, DateTime nowUtc)
    {
        if (!my.Enabled || my.OnTarget) return false;
        if (my.Target?.Key is not { Length: > 0 } key) return false;
        if (lastJoinKey != key || lastJoinUtc is null) return true;
        return nowUtc - lastJoinUtc.Value >= JoinMinInterval;
    }

    public static bool IsSeedCompleted(bool targetWasSeen, AutoseedMyResponse my) =>
        my.Enabled && my.AllSeeded == true;
}
