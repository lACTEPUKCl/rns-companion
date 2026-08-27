namespace RnsCompanion.Services;

internal static class SteamJoinUrl
{
    public const int SquadAppId = 393380;

    public static bool IsSafe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, "steam", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "joinlobby", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo)) return false;

        var appId = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return string.Equals(appId, SquadAppId.ToString(), StringComparison.Ordinal);
    }
}
