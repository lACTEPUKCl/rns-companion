using System.IO;
using System.Text.Json;
using RnsCompanion.Models;

namespace RnsCompanion.Services;

/// <summary>Загрузка/сохранение настроек (%LocalAppData%\RNS\Companion\settings.json).</summary>
internal sealed class SettingsStore
{
    private readonly string _path = Path.Combine(LogService.DataDir, "settings.json");
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (JsonException) { return new AppSettings(); }
        catch (IOException) { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch (IOException ex) { LogService.Warn($"Не удалось сохранить настройки: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { LogService.Warn($"Не удалось сохранить настройки: {ex.Message}"); }
    }
}
