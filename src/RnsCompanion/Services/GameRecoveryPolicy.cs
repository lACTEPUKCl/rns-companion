namespace RnsCompanion.Services;

/// <summary>Conservative watchdog: transient loading/network failures get time to recover.</summary>
internal sealed class GameRecoveryPolicy
{
    private DateTime? _unresponsiveSince;
    private DateTime? _disconnectedSince;
    private DateTime? _lastRestart;
    private DateTime? _processStart;

    public string? GetRestartReason(bool running, bool responding, bool ready,
        bool onTarget, DateTime? processStart, DateTime now, bool fatalError = false,
        DateTime? onlineDisconnectedSinceUtc = null)
    {
        if (_processStart != processStart || !running)
        {
            _processStart = processStart;
            _unresponsiveSince = null;
            _disconnectedSince = null;
        }
        if (!running) return null;
        _unresponsiveSince = responding ? null : _unresponsiveSince ?? now;
        _disconnectedSince = onTarget || !ready ? null : _disconnectedSince ?? now;
        if (_lastRestart is { } last && now - last < TimeSpan.FromMinutes(5)) return null;
        if (fatalError) return "В логе текущего запуска Squad обнаружен сбой";
        if (!onTarget && onlineDisconnectedSinceUtc is { } offline &&
            processStart is { } sessionStart && offline >= sessionStart &&
            now - offline >= TimeSpan.FromMinutes(2))
            return "Squad потерял связь с онлайн-сервисами более 2 минут назад";
        if (_unresponsiveSince is { } hung && now - hung >= TimeSpan.FromMinutes(2))
            return "Squad не отвечает более 2 минут";
        if (!ready && !onTarget && processStart is { } start && now - start >= TimeSpan.FromMinutes(10))
            return "Squad не загрузил меню за 10 минут";
        if (_disconnectedSince is { } disconnected && now - disconnected >= TimeSpan.FromMinutes(15))
            return "Вход на сервер не подтверждён за 15 минут повторных попыток";
        return null;
    }

    public void RecordRestart(DateTime now)
    {
        _lastRestart = now;
        _unresponsiveSince = null;
        _disconnectedSince = null;
    }
}
