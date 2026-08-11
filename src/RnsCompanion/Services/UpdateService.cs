using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace RnsCompanion.Services;

internal sealed record UpdateInfo(Version Version, string ExeUrl, string? ShaUrl, string ReleasePage);

/// <summary>
/// Автообновление: сверка с последним релизом на GitHub, скачивание нового exe,
/// проверка SHA-256 (ассет RNS.Companion.exe.sha256 из релиза) и самозамена через
/// cmd-скрипт, который ждёт выхода процесса, подменяет exe и запускает его снова.
///
/// Проверка версии — БЕЗ GitHub API: у api.github.com лимит 60 запросов/час на IP,
/// а у пользователей за cgNAT он общий. Берём редирект страницы /releases/latest
/// (302 → /releases/tag/vX.Y.Z) — веб-эндпоинты GitHub так не лимитируются.
/// </summary>
internal static class UpdateService
{
    public const string ReleasesPage = "https://github.com/lACTEPUKCl/rns-companion/releases";
    public const string ExeName = "RNS.Companion.exe";
    private const string LatestPage = ReleasesPage + "/latest";
    private const string DownloadBase = LatestPage + "/download/";

    private static readonly HttpClient Http = CreateClient();
    // Скачивание ~150 МБ — отдельный клиент с большим таймаутом.
    private static readonly HttpClient HttpDownload = CreateClient(TimeSpan.FromMinutes(15));

    private static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("RNS-Companion-Updater");
        return h;
    }

    /// <summary>Проверить наличие новой версии. null — актуальная версия или ошибка сети.</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        try
        {
            // /releases/latest отвечает 302 на /releases/tag/vX.Y.Z — тег из Location.
            using var resp = await Http.GetAsync(LatestPage, ct);
            var location = resp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location)) return null;
            var tag = location.TrimEnd('/').Split('/').Last();
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            if (latest <= current) return null;
            return new UpdateInfo(latest, DownloadBase + ExeName, DownloadBase + ExeName + ".sha256",
                $"{ReleasesPage}/tag/{tag}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null; // сеть — проверим при следующем проходе
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
