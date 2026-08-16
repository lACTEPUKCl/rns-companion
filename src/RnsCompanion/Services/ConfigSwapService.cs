using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace RnsCompanion.Services;

/// <summary>
/// Crash-safe подмена GameUserSettings.ini на встроенный low-graphics пресет
/// СТРОГО на время игровой сессии.
///
/// Модель (подтверждено практикой): игра читает INI при старте и ПЕРЕЗАПИСЫВАЕТ
/// его из памяти при выходе — содержимому файла, пока/после того как игра
/// работала с подменённым конфигом, доверять нельзя. Поэтому:
///  • подменяем только непосредственно перед запуском игры (join-ссылка) и
///    только если игра в этот момент НЕ запущена;
///  • восстанавливаем только ПОСЛЕ полного исчезновения процесса игры
///    (+ пауза, чтобы игра дописала файл).
///
/// Слои защиты оригинала:
///  1. Два бэкапа с SHA256 оригинала: GameUserSettings.rnsbak.ini (рядом с
///     оригиналом) и %LocalAppData%\RNS\Companion\backup\GameUserSettings.ini.
///     Подмена выполняется, только если хотя бы один бэкап создан и проверен.
///  2. Маркер swap-state.json — restore при следующем запуске приложения.
///  3. Exit-watcher внутри приложения — restore после выхода игры.
///  4. Detached watchdog (/watchdog) — restore, если приложение убили.
///  5. Задача планировщика «RNS Companion RestoreGuard» (при входе в систему)
///     — restore, даже если приложение больше никогда не запускали.
/// </summary>
internal sealed class ConfigSwapService
{
    public static readonly ConfigSwapService Instance = new();

    private const string IniFileName = "GameUserSettings.ini";
    private const string BackupFileName = "GameUserSettings.rnsbak.ini";
    private const string HashFileName = "backup.sha256";
    private const string WatchdogPidFileName = "watchdog.pid";
    private const string PresetResourceName = "RnsCompanion.Presets.GameUserSettings.low.ini";

    /// <summary>Пауза после исчезновения процесса игры — игра дописывает INI при выходе.</summary>
    private static readonly TimeSpan GameExitGrace = TimeSpan.FromSeconds(4);

    /// <summary>Сколько ждём появления процесса игры после подмены, прежде чем откатить её.</summary>
    private static readonly TimeSpan GameAppearTimeout = TimeSpan.FromMinutes(15);

    private readonly string _dataDir;
    private readonly string _markerPath;
    private readonly string _secondBackupPath;
    private readonly string _hashPath;
    private readonly string _watchdogPidPath;
    private readonly string? _iniPathOverride; // для тестов — чтобы не трогать реальный конфиг
    private readonly Func<bool> _isGameRunning; // шов для тестов

    private int _watcherRunning;

    public ConfigSwapService(string? iniPathOverride = null, string? dataDir = null,
        Func<bool>? isGameRunning = null)
    {
        _dataDir = dataDir ?? LogService.DataDir;
        _markerPath = Path.Combine(_dataDir, "swap-state.json");
        _secondBackupPath = Path.Combine(_dataDir, "backup", IniFileName);
        _hashPath = Path.Combine(_dataDir, "backup", HashFileName);
        _watchdogPidPath = Path.Combine(_dataDir, WatchdogPidFileName);
        _iniPathOverride = iniPathOverride;
        _isGameRunning = isGameRunning ?? DefaultGameProbe;
    }

    /// <summary>Проверка «игра запущена». Для тестовых/сторожевых режимов можно
    /// принудительно задать через переменную окружения RNS_COMPANION_GAME_RUNNING=0|1.</summary>
    internal bool GameIsRunning() => _isGameRunning();

    private static bool DefaultGameProbe() =>
        Environment.GetEnvironmentVariable("RNS_COMPANION_GAME_RUNNING") switch
        {
            "0" => false,
            "1" => true,
            _ => GameProcessService.IsGameRunning(),
        };

    public bool IsSwapActive => File.Exists(_markerPath);

    /// <summary>Путь к GameUserSettings.ini (ищем в Windows и WindowsNoEditor).</summary>
    public string ResolveIniPath()
    {
        if (_iniPathOverride is not null) return _iniPathOverride;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Path.Combine(local, "SquadGame", "Saved", "Config", "Windows", IniFileName);
        if (File.Exists(windows)) return windows;
        var noEditor = Path.Combine(local, "SquadGame", "Saved", "Config", "WindowsNoEditor", IniFileName);
        if (File.Exists(noEditor)) return noEditor;
        return windows; // дефолт — игра сама создаст
    }

    // ─────────────────────────── Подмена ───────────────────────────

    /// <summary>
    /// Применить пресет. Возвращает false, если подмена не выполнена
    /// (игра запущена, INI не существует, бэкапы не создались) — набор продолжается без low-graphics.
    /// </summary>
    public bool ApplyLowPreset(bool spawnWatchdog = true, bool registerGuard = true)
    {
        if (IsSwapActive)
        {
            LogService.Info("ConfigSwap: подмена уже активна, пропускаю.");
            return true;
        }

        // Игра перезапишет INI при выходе содержимым из памяти — подменять под работающую игру нельзя.
        if (GameIsRunning())
        {
            LogService.Info("ConfigSwap: игра уже запущена — подмена отложена до следующего цикла.");
            return false;
        }

        var iniPath = ResolveIniPath();
        if (!File.Exists(iniPath))
        {
            LogService.Warn($"ConfigSwap: {iniPath} не найден (игра ни разу не запускалась?) — подмена пропущена.");
            return false;
        }

        // 1. Два бэкапа с хэшем оригинала. Без валидного бэкапа — не подменяем вообще.
        string originalHash;
        try
        {
            originalHash = Sha256Hex(iniPath);
        }
        catch (IOException ex)
        {
            LogService.Error($"ConfigSwap: не удалось прочитать {iniPath}", ex);
            return false;
        }

        var primaryBak = Path.Combine(Path.GetDirectoryName(iniPath)!, BackupFileName);
        var okPrimary = TryCreateBackup(iniPath, primaryBak, originalHash);
        var okSecond = TryCreateBackup(iniPath, _secondBackupPath, originalHash);

        if (!okPrimary && !okSecond)
        {
            LogService.Error("ConfigSwap: не создан ни один валидный бэкап — подмена ОТМЕНЕНА (набор без low-graphics).");
            return false;
        }
        if (!okPrimary || !okSecond)
            LogService.Warn($"ConfigSwap: создан только один бэкап (primary={okPrimary}, second={okSecond}).");

        try { AtomicWriteText(_hashPath, originalHash); }
        catch (IOException ex) { LogService.Warn($"ConfigSwap: не удалось записать хэш-файл: {ex.Message}"); }

        // 2. Атомарно пишем пресет поверх INI.
        try
        {
            ClearReadOnly(iniPath);
            AtomicWriteText(iniPath, LoadPresetText());
        }
        catch (IOException ex)
        {
            LogService.Error("ConfigSwap: не удалось записать пресет — подмена отменена.", ex);
            return false;
        }

        // 3. Маркер состояния.
        WriteMarker(new SwapMarker
        {
            IniPath = iniPath,
            BackupPath = primaryBak,
            SecondBackupPath = _secondBackupPath,
            OriginalHash = originalHash,
            AppliedAtUtc = DateTime.UtcNow,
        });
        LogService.Info($"ConfigSwap: применён low-graphics пресет (бэкапы проверены, sha256={originalHash[..12]}…).");

        // 4. Страховки.
        if (spawnWatchdog) EnsureWatchdog();
        EnsureExitWatcher();
        if (registerGuard) TryRegisterRestoreGuard();
        return true;
    }

    // ─────────────────────────── Восстановление ───────────────────────────

    /// <summary>
    /// Восстановить INI из бэкапа, если маркер активен. Идемпотентно.
    /// Если игра ещё запущена — восстановление откладывается (exit-watcher).
    /// </summary>
    public void RestoreIfNeeded(string reason)
    {
        var marker = ReadMarker();
        if (marker is null) return;

        if (GameIsRunning())
        {
            LogService.Info($"ConfigSwap: восстановление ({reason}) отложено — игра ещё запущена.");
            EnsureExitWatcher();
            return;
        }

        var iniPath = marker.IniPath ?? ResolveIniPath();
        var candidates = new[]
        {
            marker.BackupPath ?? Path.Combine(Path.GetDirectoryName(iniPath)!, BackupFileName),
            marker.SecondBackupPath ?? _secondBackupPath,
        };
        var expectedHash = marker.OriginalHash ?? ReadHashFile();
        if (expectedHash is null)
            LogService.Warn("ConfigSwap: хэш оригинала неизвестен (старый маркер) — восстанавливаю без проверки.");

        foreach (var backup in candidates)
        {
            if (!File.Exists(backup)) continue;
            if (expectedHash is not null && !HashMatches(backup, expectedHash))
            {
                LogService.Warn($"ConfigSwap: бэкап {backup} не прошёл проверку хэша — пробую следующий.");
                continue;
            }
            try
            {
                ClearReadOnly(iniPath);
                AtomicCopy(backup, iniPath);
                File.SetLastWriteTimeUtc(iniPath, File.GetLastWriteTimeUtc(backup));
                LogService.Info($"ConfigSwap: оригинальный конфиг восстановлен из {Path.GetFileName(backup)} ({reason}).");
                CleanupSwapArtifacts(candidates);
                return;
            }
            catch (IOException ex)
            {
                LogService.Error($"ConfigSwap: не удалось записать {iniPath}", ex);
                return; // маркер оставляем — попробуем при следующем запуске/входе
            }
            catch (UnauthorizedAccessException ex)
            {
                LogService.Error($"ConfigSwap: не удалось записать {iniPath}", ex);
                return; // маркер оставляем — попробуем при следующем запуске/входе
            }
        }

        // Оба бэкапа битые/отсутствуют — текущий файл НЕ трогаем, маркер оставляем.
        LogService.Error("ConfigSwap: оба бэкапа отсутствуют или повреждены — файл не изменён, " +
                         "маркер оставлен для повторной попытки. Ручное восстановление: переименуйте " +
                         "GameUserSettings.rnsbak.ini (если он цел) в GameUserSettings.ini.");
    }

    private void CleanupSwapArtifacts(string[] backupPaths)
    {
        foreach (var p in backupPaths) TryDelete(p);
        TryDelete(_hashPath);
        TryDelete(_markerPath);
    }

    // ─────────────────── Exit-watcher (внутри приложения) ───────────────────

    /// <summary>
    /// Фоновая задача: ждёт старта игры (макс 15 мин), затем — её полного
    /// исчезновения, выдерживает паузу на дописывание INI и восстанавливает.
    /// </summary>
    public void EnsureExitWatcher()
    {
        if (Interlocked.Exchange(ref _watcherRunning, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                // Фаза 1: ждём появления игры (join мог не сработать — тогда откатываем подмену).
                var deadline = DateTime.UtcNow + GameAppearTimeout;
                while (IsSwapActive && !GameIsRunning() && DateTime.UtcNow < deadline)
                    await Task.Delay(3000);

                // Фаза 2: ждём ПОЛНОГО исчезновения процесса игры.
                while (IsSwapActive && GameIsRunning())
                    await Task.Delay(3000);

                if (IsSwapActive)
                {
                    await Task.Delay(GameExitGrace); // игра дописывает INI при выходе
                    if (!GameIsRunning())
                        RestoreIfNeeded("игра завершилась");
                }
            }
            catch (Exception ex)
            {
                // Watcher не должен умирать молча — иначе восстановление потеряно
                // до следующего запуска приложения/входа в систему.
                LogService.Warn($"ConfigSwap: exit-watcher завершился с ошибкой: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _watcherRunning = 0;
            }
        });
    }

    /// <summary>Точка восстановления при старте приложения.</summary>
    public void StartupRecover()
    {
        if (!IsSwapActive) return;
        // Watchdog живёт максимум 24ч — если он истёк за время долгого сида,
        // перезапускаем его при каждом старте приложения с активной подменой.
        EnsureWatchdog();
        if (GameIsRunning())
        {
            LogService.Info("ConfigSwap: подмена активна, игра запущена — восстановлю после её выхода.");
            EnsureExitWatcher();
        }
        else
        {
            RestoreIfNeeded("запуск приложения");
        }
    }

    // ─────────────────── Watchdog (detached-процесс) ───────────────────

    private void EnsureWatchdog()
    {
        try
        {
            if (File.Exists(_watchdogPidPath) &&
                int.TryParse(File.ReadAllText(_watchdogPidPath).Trim(), out var wpid) &&
                ProcessAlive(wpid))
                return; // уже сторожит

            var exe = Environment.ProcessPath;
            if (exe is null) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"/watchdog {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            LogService.Info("ConfigSwap: watchdog-процесс запущен.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            LogService.Warn($"ConfigSwap: watchdog не запустился: {ex.Message}");
        }
    }

    internal void WriteWatchdogPid()
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            File.WriteAllText(_watchdogPidPath, Environment.ProcessId.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool ProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    // ─────────────────── RestoreGuard (вход в систему) ───────────────────

    private void TryRegisterRestoreGuard()
    {
        try { SchedulerService.RegisterRestoreGuard(); }
        catch (InvalidOperationException ex) { LogService.Warn($"ConfigSwap: RestoreGuard не зарегистрирован: {ex.Message}"); }
    }

    // ─────────────────────────── internals ───────────────────────────

    private SwapMarker? ReadMarker()
    {
        if (!File.Exists(_markerPath)) return null;
        try { return JsonSerializer.Deserialize<SwapMarker>(File.ReadAllText(_markerPath)); }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LogService.Warn($"ConfigSwap: маркер повреждён ({ex.Message}) — пытаюсь восстановить по умолчаниям.");
            return new SwapMarker(); // пути по умолчанию
        }
    }

    private void WriteMarker(SwapMarker marker) =>
        AtomicWriteText(_markerPath, JsonSerializer.Serialize(marker));

    private string? ReadHashFile()
    {
        try { return File.Exists(_hashPath) ? File.ReadAllText(_hashPath).Trim() : null; }
        catch (IOException) { return null; }
    }

    private static bool HashMatches(string path, string expectedHash)
    {
        try { return string.Equals(Sha256Hex(path), expectedHash, StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string Sha256Hex(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private bool TryCreateBackup(string iniPath, string backupPath, string expectedHash)
    {
        try
        {
            AtomicCopy(iniPath, backupPath);
            File.SetLastWriteTimeUtc(backupPath, File.GetLastWriteTimeUtc(iniPath));
            return HashMatches(backupPath, expectedHash);
        }
        catch (IOException ex)
        {
            LogService.Warn($"ConfigSwap: не удалось создать бэкап {backupPath}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            LogService.Warn($"ConfigSwap: не удалось создать бэкап {backupPath}: {ex.Message}");
            return false;
        }
    }

    private static string LoadPresetText()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(PresetResourceName)
            ?? throw new InvalidOperationException($"Встроенный ресурс {PresetResourceName} не найден.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void AtomicWriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static void AtomicCopy(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tmp = dst + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal sealed class SwapMarker
    {
        public string? IniPath { get; set; }
        public string? BackupPath { get; set; }
        public string? SecondBackupPath { get; set; }
        public string? OriginalHash { get; set; }
        public DateTime AppliedAtUtc { get; set; }
    }
}
