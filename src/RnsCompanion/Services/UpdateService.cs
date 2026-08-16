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

        // Самообновление БЕЗ cmd-скрипта: свежескачанный exe (.new) запускается
        // в headless-режиме /apply-update, дожидается выхода этого процесса,
        // подменяет exe и запускает новую версию. cmd-путь был ненадёжен:
        // batch читался cmd.exe в OEM-кодировке, а писался в UTF-8 — кириллица
        // в путях (C:\Users\Кот\…) ломала move; скрытый cmd не переживал сон ПК.
        LogService.Info($"Update: запускаю self-update — новый exe дождётся выхода pid {Environment.ProcessId} и подменит {currentExe}");
        Process.Start(new ProcessStartInfo(newExe,
            $"/apply-update {Environment.ProcessId} \"{currentExe}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    /// <summary>Headless-режим самообновления (аргументы: /apply-update &lt;oldPid&gt; &lt;targetExe&gt;).
    /// Этот процесс — свежескачанный exe (.new): ждём выхода старого приложения,
    /// подменяем целевой exe на себя (с ретраями) и запускаем его.</summary>
    public static void ApplyUpdateMode(int oldPid, string targetPath)
    {
        var self = Environment.ProcessPath;
        var updateDir = Path.Combine(LogService.DataDir, "update");
        var okMarker = Path.Combine(updateDir, "update-ok.txt");
        var failMarker = Path.Combine(updateDir, "update-failed.txt");
        LogService.Info($"Updater: самообновление — жду выхода pid {oldPid}, цель {targetPath}");

        TryDelete(okMarker);
        TryDelete(failMarker);
        TryDelete(Path.Combine(updateDir, "apply-update.cmd")); // от старых версий

        if (self is null || !File.Exists(targetPath))
        {
            LogService.Error($"Updater: self={self ?? "null"}, цель существует={File.Exists(targetPath)} — отмена.");
            return;
        }

        // 1. Ждём выхода старого процесса (он закрывается сам через ~3 с).
        var waitDeadline = DateTime.UtcNow.AddMinutes(5);
        while (ConfigSwapService.ProcessAlive(oldPid) && DateTime.UtcNow < waitDeadline)
            Thread.Sleep(1000);
        if (ConfigSwapService.ProcessAlive(oldPid))
            LogService.Warn($"Updater: старый процесс {oldPid} не завершился за 5 мин — подменяю поверх.");

        // 2. Подмена с ретраями (до 10 мин): exe могут держать Defender/индексатор,
        //    либо пользователь уже запустил старую версию вручную — тогда после
        //    минуты неудач прибиваем экземпляр, держащий целевой файл.
        var moveDeadline = DateTime.UtcNow.AddMinutes(10);
        var killAttempted = false;
        while (true)
        {
            try
            {
                var tmp = targetPath + ".tmp";
                File.Copy(self, tmp, overwrite: true);
                File.Move(tmp, targetPath, overwrite: true);
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= moveDeadline)
                {
                    LogService.Error($"Updater: не удалось подменить exe за 10 мин: {ex.Message}");
                    TryWrite(failMarker, ex.Message);
                    return;
                }
                if (!killAttempted && DateTime.UtcNow >= moveDeadline.AddMinutes(-9))
                {
                    killAttempted = true;
                    KillInstanceHolding(targetPath);
                }
                Thread.Sleep(3000);
            }
        }

        LogService.Info("Updater: exe подменён, запускаю новую версию.");
        TryWrite(okMarker, DateTime.Now.ToString("O"));
        try
        {
            Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogService.Error("Updater: подмена удалась, но запуск новой версии не удался", ex);
        }
    }

    /// <summary>Прибить экземпляр, держащий целевой exe (точное совпадение пути):
    /// пользователь запустил старую версию вручную до окончания установки.</summary>
    private static void KillInstanceHolding(string targetPath)
    {
        foreach (var p in Process.GetProcessesByName("RNS.Companion"))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                if (!string.Equals(p.MainModule?.FileName, targetPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                LogService.Warn($"Updater: цель занята вручную запущенным экземпляром (pid {p.Id}) — завершаю его.");
                p.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryWrite(string path, string text)
    {
        try { File.WriteAllText(path, text); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
