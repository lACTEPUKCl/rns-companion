using System.Text.Json.Serialization;

namespace RnsCompanion.Models;

/// <summary>
/// Настройки приложения. Хранятся в %LocalAppData%\RNS\Companion\settings.json.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Базовый URL сайта (для тестов можно сменить на стенд).</summary>
    public string BaseUrl { get; set; } = "https://rnserver.ru";

    // ── Энергосбережение (по умолчанию выключено) ──

    /// <summary>Гасить мониторы при старте набора (ручной запуск).</summary>
    public bool MonitorOffDuringSeed { get; set; }

    /// <summary>Гасить мониторы при запуске по расписанию. По умолчанию ВКЛ —
    /// ночной набор не должен светить мониторами.</summary>
    public bool MonitorOffInScheduledMode { get; set; } = true;

    /// <summary>Подменять GameUserSettings.ini на low-graphics пресет на время набора.</summary>
    public bool LowGraphicsDuringSeed { get; set; }

    /// <summary>Закрывать игру, когда все серверы заполнены (цель пропала).</summary>
    public bool CloseGameAfterSeed { get; set; } = true;

    /// <summary>Уводить ПК в сон после завершения набора.</summary>
    public bool SleepAfterSeed { get; set; }

    /// <summary>Сворачивать в трей вместо закрытия по крестику.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Показывать журнал событий на главном окне (по умолчанию скрыт).</summary>
    public bool ShowJournal { get; set; }

    // ── Расписание ──

    public bool ScheduleEnabled { get; set; }

    /// <summary>Каждый день (true) или по выбранным дням недели (false).</summary>
    public bool ScheduleEveryDay { get; set; } = true;

    /// <summary>Будить компьютер из сна для запуска (WakeToRun).</summary>
    public bool ScheduleWakeToRun { get; set; } = true;

    /// <summary>Дни недели запуска (значения DayOfWeek: 0=Вс..6=Сб) — используются,
    /// когда ScheduleEveryDay == false.</summary>
    public List<int> ScheduleDays { get; set; } = new();

    /// <summary>Время запуска "HH:mm" (локальное).</summary>
    public string ScheduleTime { get; set; } = "18:00";

    [JsonIgnore]
    public TimeSpan ScheduleTimeOfDay =>
        TimeSpan.TryParse(ScheduleTime, out var t) ? t : new TimeSpan(18, 0, 0);
}
