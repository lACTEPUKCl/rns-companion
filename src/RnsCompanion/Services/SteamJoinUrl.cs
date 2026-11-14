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
    public static string? NormalizeEndpoint(string? value)
    {
        var parts = value?.Split(':');
        if (parts is not { Length: 2 } ||
            !System.Net.IPAddress.TryParse(parts[0], out var host) ||
            host.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            !int.TryParse(parts[1], out var port) || port < 1 || port > 65535)
            return null;
        return host + ":" + port;
    }
}
