using System.Windows;
using RnsCompanion.Models;
using RnsCompanion.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;

namespace RnsCompanion;

public partial class SettingsWindow : Window
{
    /// <summary>Итоговые настройки (валидны, если DialogResult == true).</summary>
    public AppSettings Result { get; private set; }

    private readonly (CheckBox Box, DayOfWeek Day)[] _dayBoxes;
    private bool _busy;

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        Result = current;

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
        BtnClose.Click += (_, _) => DialogResult = false;

        BtnSave.Click += (_, _) => Save();
        BtnCancel.Click += (_, _) => DialogResult = false;
        BtnDeleteSchedule.Click += (_, _) => DeleteSchedule();

        // Живое обновление строки-подтверждения — только локальная логика, без вызовов ОС.
        ChkScheduleEnabled.Checked += (_, _) => RefreshScheduleUi();
        ChkScheduleEnabled.Unchecked += (_, _) => RefreshScheduleUi();
        RbEveryDay.Checked += (_, _) => RefreshScheduleUi();
        RbCustomDays.Checked += (_, _) => RefreshScheduleUi();
        ChkWake.Checked += (_, _) => RefreshScheduleUi();
        ChkWake.Unchecked += (_, _) => RefreshScheduleUi();
        TxtTime.TextChanged += (_, _) => RefreshScheduleUi();
        foreach (var (box, _) in _dayBoxes)
        {
            box.Checked += (_, _) => RefreshScheduleUi();
            box.Unchecked += (_, _) => RefreshScheduleUi();
        }

        RefreshScheduleUi();
        LoadTaskSummaryAsync();
    }

    /// <summary>
    /// Состояние задачи планировщика читается асинхронно (schtasks/PowerShell —
    /// сотни миллисекунд): в UI-потоке это фризило окно при каждом клике.
    /// </summary>
    private void LoadTaskSummaryAsync()
    {
        TxtScheduleState.Text = "Проверяю задачу планировщика…";
        Task.Run(() => SchedulerService.GetTaskSummary())
            .ContinueWith(t => Dispatcher.Invoke(() =>
            {
                if (_busy) return;
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

    private void Save()
    {
        if (_busy) return;

        var everyDay = RbEveryDay.IsChecked == true;
        var days = everyDay
            ? Enum.GetValues<DayOfWeek>().ToList()
            : SelectedDays();

        if (ChkScheduleEnabled.IsChecked == true)
        {
            if (days.Count == 0)
            {
                MessageBox.Show(this, "Выберите хотя бы один день недели для расписания.",
                    "RNS Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!TimeSpan.TryParse(TxtTime.Text.Trim(), out _))
            {
                MessageBox.Show(this, "Время должно быть в формате ЧЧ:мм (например, 06:00).",
                    "RNS Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        Result = new AppSettings
        {
            // BaseUrl из UI убран — оставляем как было (фиксированный https://rnserver.ru,
            // поле в settings.json сохранено для отладки).
            BaseUrl = Result.BaseUrl,
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

        // Планировщик — асинхронно (schtasks + PowerShell занимают секунды).
        _busy = true;
        BtnSave.IsEnabled = false;
        BtnSave.Content = "Сохраняю…";
        var result = Result;
        Task.Run(() =>
        {
            try
            {
                if (result.ScheduleEnabled)
                    SchedulerService.Register(days, result.ScheduleTimeOfDay, result.ScheduleWakeToRun);
                else if (SchedulerService.TaskExists())
                    SchedulerService.Delete();

                // Страховка восстановления конфига при входе в систему — с первым
                // включением low-graphics (и обновляем путь к exe при каждом сохранении).
                if (result.LowGraphicsDuringSeed)
                    SchedulerService.RegisterRestoreGuard();

                return null;
            }
            catch (InvalidOperationException ex) { return ex.Message; }
        }).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            _busy = false;
            BtnSave.IsEnabled = true;
            BtnSave.Content = "Сохранить";
            if (t.Result is { } error)
            {
                LogService.Error("Планировщик: не удалось применить задачу: " + error);
                MessageBox.Show(this,
                    "Настройки сохранены, но задачу планировщика применить не удалось:\n" + error,
                    "RNS Companion", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            DialogResult = true;
        }));
    }
}
