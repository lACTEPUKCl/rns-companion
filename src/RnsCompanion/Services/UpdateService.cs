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

    private static readonly HttpClient Http = CreateClient(allowRedirect: false);
    // Скачивание ~150 МБ — отдельный клиент с большим таймаутом и РЕДИРЕКТАМИ
    // (ссылка /releases/latest/download/... ведёт на CDN через 302).
    private static readonly HttpClient HttpDownload = CreateClient(allowRedirect: true, timeout: TimeSpan.FromMinutes(15));

    private static HttpClient CreateClient(bool allowRedirect, TimeSpan? timeout = null)
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = allowRedirect })
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
    public static async Task DownloadAndSwapAsync(UpdateInfo info, CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к exe.");
        var dir = Path.Combine(LogService.DataDir, "update");
        Directory.CreateDirectory(dir);
        var newExe = Path.Combine(dir, ExeName + ".new");

        LogService.Info($"Update: скачиваю {info.ExeUrl}");
        using (var resp = await HttpDownload.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(newExe, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[256 * 1024];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        var size = new FileInfo(newExe).Length;
        if (size < 1_000_000) // exe ~150 МБ; меньше — точно не оно (страница ошибки и т.п.)
        {
            File.Delete(newExe);
            throw new InvalidOperationException($"Скачанный файл подозрительно мал ({size} байт) — обновление отменено.");
        }
        LogService.Info($"Update: скачано {size} байт");

        if (info.ShaUrl is not null)
        {
            var shaText = await HttpDownload.GetStringAsync(info.ShaUrl, ct); // download-ссылка — с редиректами
            var expected = shaText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            await using var fs = File.OpenRead(newExe);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(newExe);
                LogService.Error($"Update: хеш не сошёлся (ожидали {expected}, получили {actual})");
                throw new InvalidOperationException("Контрольная сумма обновления не сошлась — файл не применён.");
            }
            LogService.Info("Update: sha256 сошёлся");
        }

        // Скрипт ждёт завершения нашего PID, подменяет exe и перезапускает его.
        // Задержки через ping (timeout ломается без консоли), move — с ретраями:
        // свежескачанный exe могут недолго держать Defender/индексатор.
        // taskkill перед подменой: если пользователь не дождался и запустил
        // приложение вручную, работающий старый exe блокирует move — прибиваем его.
        var pid = Environment.ProcessId;
        var script = Path.Combine(dir, "apply-update.cmd");
        await File.WriteAllTextAsync(script,
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul\r\n" +
            "if %errorlevel%==0 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
            $"taskkill /F /IM \"{ExeName}\" >nul 2>&1\r\n" +
            "set /a tries=0\r\n" +
            ":move\r\n" +
            $"move /y \"{newExe}\" \"{currentExe}\" >nul 2>&1\r\n" +
            "if not errorlevel 1 goto start\r\n" +
            "set /a tries+=1\r\n" +
            "if %tries% geq 40 goto fail\r\n" +
            "ping -n 3 127.0.0.1 >nul\r\n" +
            "goto move\r\n" +
            ":start\r\n" +
            $"start \"\" \"{currentExe}\"\r\n" +
            "goto cleanup\r\n" +
            ":fail\r\n" +
            $"echo update failed > \"{Path.Combine(dir, "update-failed.txt")}\"\r\n" +
            ":cleanup\r\n" +
            "del \"%~f0\"\r\n", ct);
        LogService.Info($"Update: скрипт записан ({script}), запускаю cmd для pid {pid}");

        var ps = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        LogService.Info($"Update: cmd pid={ps?.Id.ToString() ?? "null"}");
    }
}
