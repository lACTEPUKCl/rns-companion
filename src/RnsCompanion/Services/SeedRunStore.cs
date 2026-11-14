using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace RnsCompanion.Services;

internal sealed record SeedRunSnapshot
{
    public string? BootId { get; init; }
    public bool Guest { get; init; }
    public bool Scheduled { get; init; }
    public bool WasOnTarget { get; init; }
    public DateTime? GameStartedAtUtc { get; init; }
    public string? LastJoinKey { get; init; }
    public DateTime? LastJoinUtc { get; init; }
    public string? ExpectedEndpoint { get; init; }
}

internal sealed class SeedRunStore
{
    private readonly string _path;
    private string? _lastSaved;

    public SeedRunStore(string? path = null) => _path = path ?? Path.Combine(LogService.DataDir, "active-seed.json");

    public static string? CurrentBootId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var id = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "BootId", null);
            return id is null ? null : Environment.MachineName + ":" + id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    }

    public SeedRunSnapshot? Load(string? bootId, DateTime? gameStartedAtUtc)
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var saved = JsonSerializer.Deserialize<SeedRunSnapshot>(File.ReadAllText(_path));
            if (saved is null) return null;
            var sameBoot = bootId is not null && saved.BootId == bootId;
            var sameGame = gameStartedAtUtc is { } current && saved.GameStartedAtUtc is { } previous &&
                Math.Abs((current - previous).TotalSeconds) < 1;
            return sameBoot || sameGame ? saved : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { LogService.Warn("Не удалось восстановить состояние набора: " + ex.Message); return null; }
    }

    public void Save(SeedRunSnapshot state)
    {
        var json = JsonSerializer.Serialize(state);
        if (json == _lastSaved && File.Exists(_path)) return;
        AtomicFile.WriteAllText(_path, json);
        _lastSaved = json;
    }

    public void Clear()
    {
        _lastSaved = null;
        if (File.Exists(_path)) File.Delete(_path);
    }
}
