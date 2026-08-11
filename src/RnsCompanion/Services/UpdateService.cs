using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace RnsCompanion.Services;

internal sealed record UpdateInfo(Version Version, string ExeUrl, string? ShaUrl, string ReleasePage);

/// <summary>
/// Автообновление: сверка с последним релизом на GitHub, скачивание нового exe,
/// проверка SHA-256 (ассет RNS.Companion.exe.sha256 из релиза) и самозамена через
/// cmd-скрипт, который ждёт выхода процесса, подменяет exe и запускает его снова.
/// </summary>
internal static class UpdateService
{
    public const string ReleasesPage = "https://github.com/lACTEPUKCl/rns-companion/releases";
    public const string ExeName = "RNS.Companion.exe";
    private const string LatestApi = "https://api.github.com/repos/lACTEPUKCl/rns-companion/releases/latest";

    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(30));
    // Скачивание ~150 МБ — отдельный клиент с большим таймаутом.
    private static readonly HttpClient HttpDownload = CreateClient(TimeSpan.FromMinutes(15));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var h = new HttpClient { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RNS-Companion-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>Проверить наличие новой версии. null — актуальная версия или ошибка сети/API.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestApi, ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (latest <= current) return null;

            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            string? exeUrl = null, shaUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name is null || url is null) continue;
                    if (name.Equals(ExeName, StringComparison.OrdinalIgnoreCase)) exeUrl = url;
                    else if (name.Equals(ExeName + ".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
                }
            }
            return exeUrl is null ? null : new UpdateInfo(latest, exeUrl, shaUrl, page ?? ReleasesPage);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // сеть/лимиты GitHub — проверим при следующем проходе
        }
    }

    /// <summary>
    /// Скачать новый exe, сверить SHA-256 и запустить скрипт самозамены.
    /// После успешного возврата приложение должно завершиться — скрипт дождётся
    /// выхода, подменит exe и запустит его. Ошибки (сеть, хеш) — наружу, в UI.
    /// </summary>
    public static async Task DownloadAndSwapAsync(UpdateInfo info, CancellationToken ct)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к exe.");
        var dir = Path.Combine(LogService.DataDir, "update");
        Directory.CreateDirectory(dir);
        var newExe = Path.Combine(dir, ExeName + ".new");

        await using (var src = await HttpDownload.GetStreamAsync(info.ExeUrl, ct))
        await using (var dst = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None))
            await src.CopyToAsync(dst, ct);

        if (info.ShaUrl is not null)
        {
            var shaText = await Http.GetStringAsync(info.ShaUrl, ct);
            var expected = shaText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            await using var fs = File.OpenRead(newExe);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(newExe);
                throw new InvalidOperationException("Контрольная сумма обновления не сошлась — файл не применён.");
            }
        }

        // Скрипт ждёт завершения нашего PID, подменяет exe и перезапускает его.
        var pid = Environment.ProcessId;
        var script = Path.Combine(dir, "apply-update.cmd");
        await File.WriteAllTextAsync(script,
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul\r\n" +
            "if %errorlevel%==0 ( timeout /t 1 /nobreak >nul & goto wait )\r\n" +
            $"move /y \"{newExe}\" \"{currentExe}\" >nul\r\n" +
            $"start \"\" \"{currentExe}\"\r\n" +
            "del \"%~f0\"\r\n", ct);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
