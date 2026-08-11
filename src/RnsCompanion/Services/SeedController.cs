using System.Diagnostics;
using System.Net.Http;
using RnsCompanion.Models;

namespace RnsCompanion.Services;

/// <summary>
/// Чистая логика принятия решений циклом набора (без побочных эффектов) —
/// удобно тестировать.
/// </summary>
internal static class SeedDecisions
{
    /// <summary>Минимальный интервал между повторными join-запусками на один и тот же сервер.</summary>
    public static readonly TimeSpan JoinMinInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Нужно ли сейчас запускать steam://joinlobby: режим включён, мы ещё не на цели,
    /// цель и ссылка есть, и для этого сервера join не запускался последние 2 минуты.
    /// </summary>
    public static bool ShouldLaunchJoin(
        AutoseedMyResponse my, string? lastJoinKey, DateTime? lastJoinUtc, DateTime nowUtc)
    {
        if (!my.Enabled || my.OnTarget) return false;
        if (my.Target?.Key is not { } key || string.IsNullOrWhiteSpace(my.JoinUrl)) return false;
        if (lastJoinKey != key || lastJoinUtc is null) return true;
        return nowUtc - lastJoinUtc.Value >= JoinMinInterval;
    }

    /// <summary>
    /// Набор завершён: режим включён, цель раньше была, а теперь пропала —
    /// все серверы набрали порог игроков.
    /// </summary>
    public static bool IsSeedCompleted(bool targetWasSeen, AutoseedMyResponse my) =>
        my.Enabled && targetWasSeen && my.Target is null;
}

internal enum SeedPhase
{
    Idle,        // режим выключен
    Connecting,  // набираем: подключаемся / ждём попадания на сервер
    OnTarget,    // мы на целевом сервере, идёт начисление
    Completed,   // все серверы заполнены
}

/// <summary>Снимок состояния для UI.</summary>
internal sealed class SeedState
{
    public SeedPhase Phase { get; set; } = SeedPhase.Idle;
    public TargetInfo? Target { get; set; }
    public SessionInfo? Session { get; set; }
    public bool SteamLinked { get; set; }
    /// <summary>Курс бонусов для отображения (из bonusDisplayRate, по умолчанию 5/мин).</summary>
    public int BonusRate { get; set; } = 5;
    public string StatusText { get; set; } = "Набор выключен";
    public IReadOnlyList<double> PlayersHistory { get; set; } = Array.Empty<double>();
}

/// <summary>
/// Цикл набора: POST start → poll /api/seed/my каждые 30 с →
/// join по правилам дедупликации → завершение (игра, конфиг, сон).
/// </summary>
internal sealed class SeedController
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private const int MaxHistoryPoints = 120; // 120 × 30 с ≈ 1 час

    private readonly ApiClient _api;
    private readonly Func<AppSettings> _settings;

    private CancellationTokenSource? _loop;
    private string? _lastJoinKey;
    private DateTime? _lastJoinUtc;
    private bool _targetWasSeen;
    private bool _scheduledMode;
    private readonly List<double> _history = new();
    // Пауза перед первым join после старта/возобновления: presence-кэш бэкенда
    // протухает до ~20 с, и первый полл может не видеть, что мы УЖЕ на цели —
    // без паузы приложение переподключает на тот же сервер.
    private DateTime _noJoinUntilUtc = DateTime.MinValue;

    public SeedState State { get; } = new();

    public bool IsRunning => _loop is not null;

    /// <summary>Состояние изменилось — UI должен перечитать State (вызывается из фонового потока!).</summary>
    public event Action? StateChanged;

    /// <summary>JWT перестал приниматься — нужно разлогиниться в UI.</summary>
    public event Action? AuthExpired;

    public SeedController(ApiClient api, Func<AppSettings> settings)
    {
        _api = api;
        _settings = settings;
    }

    /// <summary>Включить режим набора (POST /api/seed/start) и запустить цикл опроса.
    /// Если окно набора ещё закрыто — ждём его открытия и стартуем сами.</summary>
    public async Task StartAsync(bool scheduled, CancellationToken ct)
    {
        if (IsRunning) return;

        _loop = new CancellationTokenSource();
        _scheduledMode = scheduled;
        _targetWasSeen = false;
        _lastJoinKey = null;
        _lastJoinUtc = null;
        _history.Clear();

        try
        {
            await _api.StartSeedAsync(_loop.Token); // ApiException — наружу, в UI
        }
        catch (SeedWindowClosedException ex)
        {
            State.Phase = SeedPhase.Connecting;
            State.StatusText = $"Набор начнётся {DescribeOpening(ex.OpensAt)} — жду открытия…";
            StateChanged?.Invoke();
            LogService.Info($"Окно набора закрыто — жду открытия ({DescribeOpening(ex.OpensAt)}).");
            _ = Task.Run(() => WaitForWindowAsync(_loop.Token));
            return;
        }
        catch
        {
            StopLoop();
            throw;
        }

        ApplyStartSideEffects();
        _noJoinUntilUtc = DateTime.UtcNow.AddSeconds(60);

        State.Phase = SeedPhase.Connecting;
        State.StatusText = "Набор включён. Опрашиваю сервер…";
        StateChanged?.Invoke();

        _ = Task.Run(() => RunLoopAsync(_loop.Token));
    }

    /// <summary>Ожидание открытия окна набора (запуск по расписанию до 06:00 и т.п.):
    /// опрашиваем публичный статус раз в минуту, в лог — не чаще раза в 10 минут.</summary>
    private async Task WaitForWindowAsync(CancellationToken ct)
    {
        var lastLog = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await _api.GetStatusAsync(ct);
                if (status?.Window is { Open: true })
                {
                    try { await _api.StartSeedAsync(ct); }
                    catch (SeedWindowClosedException) { goto wait; } // окно ещё не открылось

                    LogService.Info("Окно набора открылось — режим включён.");
                    ApplyStartSideEffects();
                    State.Phase = SeedPhase.Connecting;
                    State.StatusText = "Набор включён. Опрашиваю сервер…";
                    StateChanged?.Invoke();
                    _ = Task.Run(() => RunLoopAsync(ct));
                    return;
                }

                if (DateTime.UtcNow - lastLog > TimeSpan.FromMinutes(10))
                {
                    lastLog = DateTime.UtcNow;
                    LogService.Info($"Окно набора закрыто — жду ({DescribeOpening(status?.Window?.OpensAt)})…");
                    State.StatusText = $"Набор начнётся {DescribeOpening(status?.Window?.OpensAt)} — жду открытия…";
                    StateChanged?.Invoke();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (ApiException ex) when (ex.IsAuthError)
            {
                LogService.Warn("Сервер больше не принимает токен — требуется повторный вход.");
                StopLoop();
                State.Phase = SeedPhase.Idle;
                State.StatusText = "Сессия истекла — войдите заново";
                StateChanged?.Invoke();
                AuthExpired?.Invoke();
                return;
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                if (DateTime.UtcNow - lastLog > TimeSpan.FromMinutes(10))
                {
                    lastLog = DateTime.UtcNow;
                    LogService.Warn($"Ожидание окна набора: {ex.Message} (повторяю раз в минуту).");
                }
            }

            wait:
            try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string DescribeOpening(DateTime? opensAt) =>
        opensAt is { } t ? "в " + t.ToLocalTime().ToString("HH:mm") : "в 06:00";

    /// <summary>
    /// Восстановление после перезапуска приложения: если участие активно на сервере —
    /// продолжаем цикл опроса без повторного POST start и без стартовых побочных
    /// эффектов (мониторы и т.п. уже обработаны при первом включении).
    /// </summary>
    public async Task ResumeAsync(CancellationToken ct)
    {
        if (IsRunning) return;

        AutoseedMyResponse? my;
        try { my = await _api.GetMyAsync(ct); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            return; // сеть/сервер недоступны — остаёмся в выключенном состоянии
        }
        if (my?.Enabled != true) return;

        _loop = new CancellationTokenSource();
        _scheduledMode = false;
        _targetWasSeen = my.Target is not null;
        _noJoinUntilUtc = DateTime.UtcNow.AddSeconds(60);
        LogService.Info("На сервере активно участие в наборе — продолжаю после перезапуска приложения.");
        UpdateState(my);
        _ = Task.Run(() => RunLoopAsync(_loop.Token));
    }

    /// <summary>Выключить режим (POST /api/seed/stop) и остановить цикл.</summary>
    public async Task StopAsync()
    {
        StopLoop();

        try { await _api.StopSeedAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            LogService.Warn($"POST stop не прошёл ({ex.Message}) — локально режим уже выключен.");
        }

        // Подмена конфига здесь НЕ откатывается: если игра ещё идёт, она перезапишет
        // INI при выходе — восстановление сделает exit-watcher после исчезновения процесса.

        State.Phase = SeedPhase.Idle;
        State.Session = null;
        State.StatusText = "Набор выключен";
        StateChanged?.Invoke();
    }

    /// <summary>Локальная остановка без запросов к серверу (выход из приложения).</summary>
    public void Shutdown()
    {
        StopLoop();
        // Восстановление конфига при выходе — на exit-watcher'е (игра ещё жива)
        // или watchdog'е (нас убили). Здесь ничего откатывать нельзя: игра может
        // быть запущена и перезапишет INI при выходе.
    }

    private void StopLoop()
    {
        var loop = _loop;
        _loop = null;
        if (loop is not null)
        {
            loop.Cancel();
            loop.Dispose();
        }
    }

    private void ApplyStartSideEffects()
    {
        var s = _settings();

        // Low-graphics НЕ применяется на старте набора — только непосредственно
        // перед запуском игры (см. TryApplyLowPreset перед join).

        if ((s.MonitorOffDuringSeed && !_scheduledMode) ||
            (s.MonitorOffInScheduledMode && _scheduledMode))
        {
            PowerService.MonitorsOff();
        }
    }

    /// <summary>
    /// Подмена GameUserSettings.ini на low-пресет — только перед запуском игры.
    /// Все защитные проверки (игра уже запущена, INI нет, бэкапы не создались) —
    /// внутри ConfigSwapService; при неудаче набор идёт без low-graphics.
    /// </summary>
    private void TryApplyLowPreset()
    {
        if (!_settings().LowGraphicsDuringSeed) return;
        try { ConfigSwapService.Instance.ApplyLowPreset(); }
        catch (Exception ex) { LogService.Error("Не удалось применить low-пресет — набор продолжается без него", ex); }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        LogService.Info("Цикл набора запущен (опрос каждые 30 с).");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var my = await _api.GetMyAsync(ct);
                if (my is null)
                {
                    LogService.Warn("Пустой ответ /api/seed/my.");
                }
                else
                {
                    if (!my.Enabled)
                    {
                        // Режим выключили на сервере (например, с сайта) — сворачиваемся.
                        LogService.Info("Сервер сообщил enabled=false — набор выключен извне.");
                        await StopAsync();
                        return;
                    }

                    if (my.Target is not null) _targetWasSeen = true;

                    if (SeedDecisions.IsSeedCompleted(_targetWasSeen, my))
                    {
                        await CompleteSeedAsync();
                        return;
                    }

                    if (DateTime.UtcNow >= _noJoinUntilUtc &&
                        SeedDecisions.ShouldLaunchJoin(my, _lastJoinKey, _lastJoinUtc, DateTime.UtcNow))
                    {
                        TryApplyLowPreset();
                        LaunchJoin(my.JoinUrl!, my.Target!.Key!);
                        _lastJoinKey = my.Target.Key;
                        _lastJoinUtc = DateTime.UtcNow;
                    }

                    UpdateState(my);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ApiException ex) when (ex.IsAuthError)
            {
                LogService.Warn("Сервер больше не принимает токен — требуется повторный вход.");
                StopLoop();
                // Подмена конфига не откатывается здесь — её снимет exit-watcher/watchdog.
                State.Phase = SeedPhase.Idle;
                State.StatusText = "Сессия истекла — войдите заново";
                StateChanged?.Invoke();
                AuthExpired?.Invoke();
                return;
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException)
            {
                LogService.Warn($"Ошибка опроса: {ex.Message} (повтор через 30 с).");
            }

            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void UpdateState(AutoseedMyResponse my)
    {
        State.SteamLinked = my.SteamLinked;
        State.Target = my.Target;
        State.Session = my.Session;
        State.BonusRate = my.BonusDisplayRate is > 0 ? my.BonusDisplayRate.Value : 5;

        if (my.Target is not null)
        {
            _history.Add(my.Target.Players);
            if (_history.Count > MaxHistoryPoints)
                _history.RemoveRange(0, _history.Count - MaxHistoryPoints);
            State.PlayersHistory = _history.ToArray();
        }

        if (my.OnTarget && my.Session is not null)
        {
            State.Phase = SeedPhase.OnTarget;
            State.StatusText = "Вы на целевом сервере — идёт сид";
        }
        else
        {
            State.Phase = SeedPhase.Connecting;
            State.StatusText = my.Target is null
                ? "Ожидаю цель набора…"
                : "Подключаемся к серверу… (Steam сам подключит игру из главного меню)";
        }

        StateChanged?.Invoke();
    }

    private void LaunchJoin(string joinUrl, string targetKey)
    {
        try
        {
            Process.Start(new ProcessStartInfo(joinUrl) { UseShellExecute = true });
            LogService.Info($"Запущена ссылка подключения к {targetKey}: {joinUrl}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogService.Error($"Не удалось открыть {joinUrl}", ex);
        }
    }

    private async Task CompleteSeedAsync()
    {
        LogService.Info("Цель пропала — все серверы заполнены. Завершаю набор.");
        var s = _settings();

        StopLoop();

        // Сначала закрываем игру — exit-watcher восстановит конфиг ПОСЛЕ
        // полного выхода процесса (игра допишет свой INI, потом watcher вернёт оригинал).
        if (s.CloseGameAfterSeed)
            await GameProcessService.CloseGameAsync();

        State.Phase = SeedPhase.Completed;
        State.Session = null;
        State.StatusText = "Все серверы заполнены — отличная работа!";
        StateChanged?.Invoke();

        if (s.SleepAfterSeed)
        {
            // Даём UI показать финальное состояние, потом уводим ПК в сон.
            await Task.Delay(TimeSpan.FromSeconds(15));
            PowerService.Sleep();
        }
    }
}
