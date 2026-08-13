using System.Windows;
using System.Windows.Threading;
using RnsCompanion.Models;
using RnsCompanion.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace RnsCompanion;

/// <summary>
/// Настройки с автосохранением: любое изменение сразу уходит в MainWindow
/// (персист в settings.json). Задача планировщика применяется с дебаунсом
/// (schtasks + PowerShell занимают секунды — не дёргаем на каждый клик).
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly TimeSpan ScheduleDebounce = TimeSpan.FromMilliseconds(1200);

    private readonly Action<AppSettings> _onChange;
    private readonly string _baseUrl;
    private readonly (CheckBox Box, DayOfWeek Day)[] _dayBoxes;
    private readonly DispatcherTimer _scheduleTimer;
    private bool _busy;          // удаление расписания
    private bool _scheduleBusy;  // фоновая регистрация задачи
    private bool _schedulePending;

    public SettingsWindow(AppSettings current, Action<AppSettings> onChange)
    {
        InitializeComponent();
        _onChange = onChange;
        _baseUrl = current.BaseUrl;

        _dayBoxes = new[]
        {
            (ChkMon, DayOfWeek.Monday), (ChkTue, DayOfWeek.Tuesday),
            (ChkWed, DayOfWeek.Wednesday), (ChkThu, DayOfWeek.Thursday),
            (ChkFri, DayOfWeek.Friday), (ChkSat, DayOfWeek.Saturday),
            (ChkSun, DayOfWeek.Sunday),
        };

        ChkMonitorOff.IsChecked = current.MonitorOffDuringSeed;
        ChkMonitorOffScheduled.IsChecked = current.MonitorOffInScheduledMode;
        ChkLowGraphics.IsChecked = current.LowGraphicsDuringSeed;
        ChkCloseGame.IsChecked = current.CloseGameAfterSeed;
        ChkSleep.IsChecked = current.SleepAfterSeed;
        ChkTray.IsChecked = current.MinimizeToTray;
        ChkShowJournal.IsChecked = current.ShowJournal;

        ChkScheduleEnabled.IsChecked = current.ScheduleEnabled;
        RbEveryDay.IsChecked = current.ScheduleEveryDay;
        RbCustomDays.IsChecked = !current.ScheduleEveryDay;
        ChkWake.IsChecked = current.ScheduleWakeToRun;
        TxtTime.Text = current.ScheduleTime;
        foreach (var (box, day) in _dayBoxes)
            box.IsChecked = current.ScheduleDays.Contains((int)day);

        TxtAboutVersion.Text =
            "версия " + (GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0") +
            " · rnserver.ru";

        // Кастомный chrome.
        RootBorder.SizeChanged += (_, _) => RootBorder.Clip = new System.Windows.Media.RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), 16, 16);
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        };
        BtnClose.Click += (_, _) => Close();
        BtnDeleteSchedule.Click += (_, _) => DeleteSchedule();

        // Автосохранение подключаем ПОСЛЕ инициализации контролов,
        // чтобы стартовые IsChecked не триггерили лишние записи.
        var simpleBoxes = new[]
        {
            ChkMonitorOff, ChkMonitorOffScheduled, ChkLowGraphics,
            ChkCloseGame, ChkSleep, ChkTray, ChkShowJournal,
        };
        foreach (var box in simpleBoxes)
        {
            box.Checked += (_, _) => OnSimpleChanged();
            box.Unchecked += (_, _) => OnSimpleChanged();
        }

        _scheduleTimer = new DispatcherTimer { Interval = ScheduleDebounce };
        _scheduleTimer.Tick += (_, _) =>
        {
            _scheduleTimer.Stop();
            ApplyScheduleTask();
        };

        ChkScheduleEnabled.Checked += (_, _) => OnScheduleChanged();
        ChkScheduleEnabled.Unchecked += (_, _) => OnScheduleChanged();
        RbEveryDay.Checked += (_, _) => OnScheduleChanged();
        RbCustomDays.Checked += (_, _) => OnScheduleChanged();
        ChkWake.Checked += (_, _) => OnScheduleChanged();
        ChkWake.Unchecked += (_, _) => OnScheduleChanged();
        TxtTime.TextChanged += (_, _) => OnScheduleChanged();
        foreach (var (box, _) in _dayBoxes)
        {
            box.Checked += (_, _) => OnScheduleChanged();
            box.Unchecked += (_, _) => OnScheduleChanged();
        }

        RefreshScheduleUi();
        LoadTaskSummaryAsync();
    }

    // ─────────────────────────── Автосохранение ───────────────────────────

    private AppSettings BuildSettings()
    {
        var everyDay = RbEveryDay.IsChecked == true;
        var days = everyDay
            ? Enum.GetValues<DayOfWeek>().ToList()
            : SelectedDays();
        return new AppSettings
        {
            // BaseUrl из UI убран — оставляем как было (фиксированный https://rnserver.ru,
            // поле в settings.json сохранено для отладки).
            BaseUrl = _baseUrl,
            MonitorOffDuringSeed = ChkMonitorOff.IsChecked == true,
            MonitorOffInScheduledMode = ChkMonitorOffScheduled.IsChecked == true,
            LowGraphicsDuringSeed = ChkLowGraphics.IsChecked == true,
            CloseGameAfterSeed = ChkCloseGame.IsChecked == true,
            SleepAfterSeed = ChkSleep.IsChecked == true,
            MinimizeToTray = ChkTray.IsChecked == true,
            ShowJournal = ChkShowJournal.IsChecked == true,
            ScheduleEnabled = ChkScheduleEnabled.IsChecked == true,
            ScheduleEveryDay = everyDay,
            ScheduleWakeToRun = ChkWake.IsChecked == true,
            ScheduleDays = everyDay ? new List<int>() : days.Select(d => (int)d).ToList(),
            ScheduleTime = TxtTime.Text.Trim(),
        };
    }

    private void OnSimpleChanged()
    {
        _onChange(BuildSettings());
        MarkSaved();
    }

    private void OnScheduleChanged()
    {
        RefreshScheduleUi();
        _onChange(BuildSettings()); // состояние контролов персистим сразу
        MarkSaved();
        _scheduleTimer.Stop();    // а задачу в ОС применяем, когда пользователь «докликал»
        _scheduleTimer.Start();
    }

    private void MarkSaved() =>
        TxtSaved.Text = "Сохранено ✓ " + DateTime.Now.ToString("HH:mm:ss");

    // ─────────────────── Планировщик (фон, с дебаунсом) ───────────────────

    private void ApplyScheduleTask()
    {
        if (_scheduleBusy)
        {
            _schedulePending = true;
            return;
        }

        var s = BuildSettings();
        var days = s.ScheduleEveryDay
            ? Enum.GetValues<DayOfWeek>().ToList()
            : SelectedDays();

        if (s.ScheduleEnabled)
        {
            // Невалидные дни/время — задачу не трогаем, подсказка уже в TxtScheduleConfirm.
            if (!TimeSpan.TryParse(s.ScheduleTime, out _) || days.Count == 0)
                return;
        }

        _scheduleBusy = true;
        TxtScheduleState.Text = "Применяю задачу планировщика…";
        Task.Run(() =>
        {
            try
            {
                if (s.ScheduleEnabled)
                    SchedulerService.Register(days, s.ScheduleTimeOfDay, s.ScheduleWakeToRun);
                else if (SchedulerService.TaskExists())
                    SchedulerService.Delete();

                // Страховка восстановления конфига при входе в систему — с первым
                // включением low-graphics (и обновляем путь к exe при каждом применении).
                if (s.LowGraphicsDuringSeed)
                    SchedulerService.RegisterRestoreGuard();

                return null;
            }
            catch (InvalidOperationException ex) { return ex.Message; }
        }).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            _scheduleBusy = false;
            if (t.Result is { } error)
            {
                LogService.Error("Планировщик: не удалось применить задачу: " + error);
                TxtScheduleState.Text = "Не удалось применить задачу: " + error;
            }
            LoadTaskSummaryAsync();
            if (_schedulePending)
            {
                _schedulePending = false;
                _scheduleTimer.Start();
            }
        }));
    }

    /// <summary>
    /// Состояние задачи планировщика читается асинхронно (schtasks/PowerShell —
    /// сотни миллисекунд): в UI-потоке это фризило окно при каждом клике.
    /// </summary>
    private void LoadTaskSummaryAsync()
    {
        if (!_scheduleBusy && TxtScheduleState.Text != "Применяю задачу планировщика…")
            TxtScheduleState.Text = "Проверяю задачу планировщика…";
        Task.Run(() => SchedulerService.GetTaskSummary())
            .ContinueWith(t => Dispatcher.Invoke(() =>
            {
                if (_busy || _scheduleBusy) return;
                TxtScheduleState.Text = t.Result is { } summary
                    ? $"В планировщике: {summary}"
                    : "Задача в планировщике пока не создана.";
            }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>Строка-подтверждение + доступность контролов расписания (только локальные данные).</summary>
    private void RefreshScheduleUi()
    {
        var enabled = ChkScheduleEnabled.IsChecked == true;
        ScheduleBody.IsEnabled = enabled;
        ScheduleBody.Opacity = enabled ? 1.0 : 0.45;
        DaysPanel.IsEnabled = RbCustomDays.IsChecked == true;

        TxtScheduleConfirm.Text = BuildConfirmation();
    }

    private string BuildConfirmation()
    {
        if (ChkScheduleEnabled.IsChecked != true)
            return "Автоматический запуск выключен — набор начинается только вручную.";

        var everyDay = RbEveryDay.IsChecked == true;
        var days = SelectedDays();
        var timeOk = TimeSpan.TryParse(TxtTime.Text.Trim(), out var time);
        var timeText = timeOk ? time.ToString(@"hh\:mm") : "—";

        string when;
        if (everyDay) when = "ежедневно";
        else if (days.Count == 0) when = "(выберите дни недели)";
        else when = "по " + string.Join(", ", days.Select(ShortDayName));

        var wake = ChkWake.IsChecked == true
            ? ", компьютер будет выведен из сна"
            : ", без вывода из сна (если ПК спит — запуск пропустится)";

        return timeOk && (everyDay || days.Count > 0)
            ? $"Набор игроков будет запускаться {when} в {timeText}{wake}."
            : "Заполните дни и время запуска.";
    }

    private static string ShortDayName(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "Пн", DayOfWeek.Tuesday => "Вт", DayOfWeek.Wednesday => "Ср",
        DayOfWeek.Thursday => "Чт", DayOfWeek.Friday => "Пт", DayOfWeek.Saturday => "Сб",
        _ => "Вс",
    };

    private List<DayOfWeek> SelectedDays() =>
        _dayBoxes.Where(d => d.Box.IsChecked == true).Select(d => d.Day).ToList();

    private void DeleteSchedule()
    {
        if (_busy) return;
        _busy = true;
        BtnDeleteSchedule.IsEnabled = false;
        Task.Run(() =>
        {
            try
            {
                if (SchedulerService.TaskExists()) SchedulerService.Delete();
                return null;
            }
            catch (InvalidOperationException ex) { return ex.Message; }
        }).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            _busy = false;
            BtnDeleteSchedule.IsEnabled = true;
            if (t.Result is { } error)
            {
                LogService.Error("Планировщик: не удалось удалить задачу: " + error);
                MessageBox.Show(this, "Не удалось удалить задачу планировщика:\n" + error,
                    "RNS Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            ChkScheduleEnabled.IsChecked = false;
            RefreshScheduleUi();
            LoadTaskSummaryAsync();
        }));
    }
}
