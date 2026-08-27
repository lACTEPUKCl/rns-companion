using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

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
            if (expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
            {
                File.Delete(newExe);
                throw new InvalidOperationException("Файл контрольной суммы обновления имеет неверный формат.");
            }
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

        VerifyUpdateSignature(currentExe, newExe);

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

        var validationError = self is null ? "не удалось определить путь helper-а" : "";
        if (self is null || !ValidateUpdateInvocation(self, targetPath, oldPid, updateDir, out validationError))
        {
            LogService.Error($"Updater: небезопасные параметры запуска — {validationError}. Обновление отменено.");
            TryWrite(failMarker, validationError);
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

    /// <summary>
    /// Совместимо и со старыми версиями инициатора: marker-файл не требуется.
    /// Проверяем фактические пути helper-а, цели и EXE живого родительского PID.
    /// Вся работа остаётся через .NET File API, поэтому кириллица в путях безопасна.
    /// </summary>
    private static bool ValidateUpdateInvocation(
        string self, string targetPath, int oldPid, string updateDir, out string error)
    {
        error = "неизвестная ошибка";
        try
        {
            var selfFull = Path.GetFullPath(self);
            var expectedSelf = Path.GetFullPath(Path.Combine(updateDir, ExeName + ".new"));
            var targetFull = Path.GetFullPath(targetPath);
            if (!string.Equals(selfFull, expectedSelf, StringComparison.OrdinalIgnoreCase))
            {
                error = "helper запущен не из каталога обновлений";
                return false;
            }
            if (!File.Exists(targetFull))
            {
                error = "целевой EXE не найден";
                return false;
            }

            using var parent = Process.GetProcessById(oldPid);
            var parentExe = parent.MainModule?.FileName;
            if (parent.HasExited || string.IsNullOrWhiteSpace(parentExe) ||
                !string.Equals(Path.GetFullPath(parentExe), targetFull, StringComparison.OrdinalIgnoreCase))
            {
                error = "цель не совпадает с EXE родительского процесса";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or InvalidOperationException or
                                   System.ComponentModel.Win32Exception)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void VerifyUpdateSignature(string currentExe, string newExe)
    {
        var currentSigner = TryGetSignerName(currentExe);
        var newSigner = TryGetSignerName(newExe);

        // Старые релизы могли быть неподписанными. Им оставляем миграцию по SHA256,
        // но как только пользователь находится на подписанной сборке, downgrade к
        // неподписанному/чужому издателю запрещён.
        if (currentSigner is null)
        {
            if (newSigner is not null && !HasTrustedAuthenticodeSignature(newExe))
                throw new InvalidOperationException("Цифровая подпись обновления недействительна.");
            LogService.Info(newSigner is null
                ? "Update: текущая и новая сборки без подписи — используется совместимый режим SHA256"
                : $"Update: подпись новой сборки действительна ({newSigner})");
            return;
        }

        if (newSigner is null || !HasTrustedAuthenticodeSignature(newExe))
            throw new InvalidOperationException("Подписанная установка не может быть обновлена неподписанным файлом.");
        if (!string.Equals(currentSigner, newSigner, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Издатель обновления не совпадает с установленной версией ({newSigner} вместо {currentSigner}).");
        LogService.Info($"Update: Authenticode-подпись и издатель проверены ({newSigner})");
    }

    private static string? TryGetSignerName(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            var name = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (CryptographicException) { return null; }
    }

    private static bool HasTrustedAuthenticodeSignature(string path)
    {
        var fileInfo = new WinTrustFileInfo
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = path,
        };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        var dataPtr = IntPtr.Zero;
        var fileInitialized = false;
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            fileInitialized = true;
            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,       // WTD_UI_NONE
                UnionChoice = 1,    // WTD_CHOICE_FILE
                FileInfo = filePtr,
                StateAction = 0,    // WTD_STATEACTION_IGNORE
                ProviderFlags = 0x100, // WTD_SAFER_FLAG
            };
            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPtr, false);
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new IntPtr(-1), ref action, dataPtr) == 0;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
            if (fileInitialized) Marshal.DestroyStructure<WinTrustFileInfo>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr trustData);

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
