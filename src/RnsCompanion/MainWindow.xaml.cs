using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using RnsCompanion.Models;
using RnsCompanion.Services;
using WinForms = System.Windows.Forms;

namespace RnsCompanion;

public partial class MainWindow : Window
{
    private static readonly TimeSpan PublicStatusInterval = TimeSpan.FromSeconds(60);
    private const int MaxJournalLines = 300;

    private readonly ApiClient _api = new();
    private readonly TokenStore _tokens = new();
    private readonly SettingsStore _settingsStore = new();
    private AppSettings _settings;
    private readonly SeedController _seed;
    private readonly DispatcherTimer _uiTimer;
    private readonly CancellationTokenSource _statusPoll = new();

    private WinForms.NotifyIcon? _tray;
    private bool _reallyClose;
    private bool _seedStartInProgress;
    private bool? _windowOpen; // из публичного статуса; null — бэкенд старый, не знаем
    private int _threshold; // порог заполнения сервера (из публичного статуса)
    // Последний публичный статус — источник карточки серверов (живёт всегда,
    // независимо от того, включён ли личный набор и залогинен ли пользователь).
    private AutoseedStatusResponse? _lastStatus;

    public MainWindow(bool scheduledLaunch)
    {
        InitializeComponent();

        _settings = _settingsStore.Load();
        _api.BaseUrl = _settings.BaseUrl;
        _seed = new SeedController(_api, () => _settings);
        _seed.StateChanged += () => Dispatcher.Invoke(RefreshSeedUi);
        _seed.AuthExpired += () => Dispatcher.Invoke(() =>
        {
            AppendJournal("Сессия истекла — войдите заново.");
            ShowLoggedOut();
        });

        Icon = LogoService.Logo;
        TxtVersion.Text = "v" + (GetType().Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

        // Кастомный chrome: скругление по факту размера, перетаскивание, min/close.
        RootBorder.SizeChanged += (_, _) => RootBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, RootBorder.ActualWidth, RootBorder.ActualHeight), 16, 16);
        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        };
        SidebarBorder.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
                DragMove();
        };
        BtnMinimize.Click += (_, _) => WindowState = WindowState.Minimized;
        BtnClose.Click += (_, _) => Close();

        // Навигация по страницам (сайдбар)
        NavSeed.Checked += (_, _) => ShowPage(Page.Seed);
        NavVip.Checked += (_, _) => ShowPage(Page.Vip);
        NavJournal.Checked += (_, _) => ShowPage(Page.Journal);

        InitTray();

        // Предзаполняем журнал строками сегодняшнего лога, затем подписываемся на новые.
        foreach (var line in LogService.ReadRecentLines(80))
            AppendJournal(line);
        LogService.LineWritten += line =>
        {
            try { Dispatcher.BeginInvoke(() => AppendJournal(line)); }
            catch (TaskCanceledException) { /* окно уже закрывается */ }
        };

        BtnLogin.Click += (_, _) => StartBrowserLogin();
        BtnLogout.Click += (_, _) => Logout();
        BtnStart.Click += async (_, _) => await StartSeedAsync(scheduled: false);
        BtnStop.Click += async (_, _) => await StopSeedAsync();
        BtnSettings.Click += (_, _) => OpenSettings();
        BtnSite.Click += (_, _) => OpenUrl(_settings.BaseUrl + "/seed");
        BtnBuyVip.Click += async (_, _) => await BuyVipAsync();
        BtnUpdate.Click += async (_, _) => await ApplyUpdateAsync();
        BtnUpdateLater.Click += (_, _) =>
        {
            _updateDismissed = _pendingUpdate?.Version;
            UpdateBar.Visibility = Visibility.Collapsed;
        };

        RestoreAuth();
        NavJournal.Visibility = _settings.ShowJournal ? Visibility.Visible : Visibility.Collapsed;

        // Если набор был включён до перезапуска приложения — подхватываем его.
        if (_api.Token is not null)
            _ = _seed.ResumeAsync(CancellationToken.None);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => TickUi();
        _uiTimer.Start();

        _ = Task.Run(() => PublicStatusLoopAsync(_statusPoll.Token));
        _ = Task.Run(() => UpdateCheckLoopAsync(_statusPoll.Token));

        if (scheduledLaunch)
        {
            AppendJournal("Запуск по расписанию: автоматически включаю набор…");
            if (_api.Token is null)
                AppendJournal("Нет сохранённой авторизации — войдите вручную, затем расписание будет работать.");
            else
                _ = StartSeedAsync(scheduled: true);
        }
    }

    // ─────────────────────────── Авторизация ───────────────────────────

    private void RestoreAuth()
    {
        var state = _tokens.Load();
        if (state?.Token is { Length: > 0 } token && !IsJwtExpired(token))
        {
            _api.Token = token;
            ShowLoggedIn(token);
            AppendJournal("Авторизация восстановлена из защищённого хранилища (DPAPI).");
        }
        else
        {
            if (state is not null) _tokens.Clear();
            ShowLoggedOut();
        }
    }

    private void StartBrowserLogin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_api.BuildAuthUrl()) { UseShellExecute = true });
            TxtAuthProgress.Text = "Жду входа в браузере…";
            TxtAuthProgress.Visibility = Visibility.Visible;
            AppendJournal("Открыт браузер для входа на сайт. После входа вы вернётесь в приложение.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            AppendJournal($"Не удалось открыть браузер: {ex.Message}");
        }
    }

    /// <summary>Вызывается при получении rnscompanion://auth#code=... (из аргументов или второго экземпляра).</summary>
    public void HandleProtocolUri(string uri)
    {
        ShowAndActivate();
        var code = ExtractCode(uri);
        if (code is null)
        {
            AppendJournal($"Получен неизвестный URI: {uri}");
            return;
        }
        _ = ExchangeCodeAsync(code);
    }

    private async Task ExchangeCodeAsync(string code)
    {
        try
        {
            var token = await _api.ExchangeCodeAsync(code, CancellationToken.None);
            _api.Token = token;
            _tokens.Save(new AuthState { Token = token });
            ShowLoggedIn(token);
            AppendJournal("Вход выполнен. Токен сохранён в защищённом хранилище.");
            await _seed.ResumeAsync(CancellationToken.None); // вдруг набор уже активен на сервере
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            TxtAuthProgress.Text = "Ошибка входа — попробуйте ещё раз.";
            AppendJournal($"Ошибка обмена кода: {ex.Message}");
        }
    }

    private void Logout()
    {
        _ = StopSeedAsync();
        _api.Token = null;
        _tokens.Clear();
        ShowLoggedOut();
        AppendJournal("Вы вышли из аккаунта. Токен удалён.");
    }

    private void ShowLoggedIn(string token)
    {
        LoginPanel.Visibility = Visibility.Collapsed;
        UserPanel.Visibility = Visibility.Visible;
        VipCard.Visibility = Visibility.Visible;
        VipLoginHint.Visibility = Visibility.Collapsed;
        SessionCard.Visibility = Visibility.Visible;
        TxtAccount.Text = DecodeJwtClaim(token, "username") ?? "игрок";
        BtnStart.IsEnabled = true;
    }

    private void ShowLoggedOut()
    {
        LoginPanel.Visibility = Visibility.Visible;
        UserPanel.Visibility = Visibility.Collapsed;
        VipCard.Visibility = Visibility.Collapsed;
        VipLoginHint.Visibility = Visibility.Visible;
        SessionCard.Visibility = Visibility.Collapsed;
        TxtAuthProgress.Visibility = Visibility.Collapsed;
        BtnStart.IsEnabled = false;
    }

    // ─────────────────────────── Навигация (сайдбар) ───────────────────────────

    private enum Page { Seed, Vip, Journal }

    private void ShowPage(Page page)
    {
        PageSeed.Visibility = page == Page.Seed ? Visibility.Visible : Visibility.Collapsed;
        PageVip.Visibility = page == Page.Vip ? Visibility.Visible : Visibility.Collapsed;
        PageJournal.Visibility = page == Page.Journal ? Visibility.Visible : Visibility.Collapsed;
        TxtPageTitle.Text = page switch
        {
            Page.Vip => "VIP и бонусы",
            Page.Journal => "Журнал",
            _ => "Набор игроков",
        };
    }

    private static string? ExtractCode(string uri)
    {
        // rnscompanion://auth#code=... (код во fragment, чтобы не светился в логах прокси)
        var marker = "#code=";
        var idx = uri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return Uri.UnescapeDataString(uri[(idx + marker.Length)..]);
        marker = "code=";
        idx = uri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? Uri.UnescapeDataString(uri[(idx + marker.Length)..].TrimEnd('/')) : null;
    }

    private static bool IsJwtExpired(string token)
    {
        var exp = DecodeJwtClaim(token, "exp");
        if (exp is null || !long.TryParse(exp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return false; // не смогли прочитать — пусть решает сервер
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > seconds;
    }

    private static string? DecodeJwtClaim(string token, string claim)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!doc.RootElement.TryGetProperty(claim, out var el)) return null;
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    // ─────────────────────────── Набор ───────────────────────────

    private async Task StartSeedAsync(bool scheduled)
    {
        if (_seedStartInProgress || _seed.IsRunning) return;
        _seedStartInProgress = true;
        BtnStart.IsEnabled = false;
        try
        {
            await _seed.StartAsync(scheduled, CancellationToken.None);
            AppendJournal(scheduled
                ? "Набор игроков включён (запуск по расписанию)."
                : "Набор игроков включён.");
        }
        catch (ApiException ex) when (ex.IsAuthError)
        {
            AppendJournal("Сессия истекла — войдите заново.");
            ShowLoggedOut();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            AppendJournal($"Не удалось включить режим: {ex.Message}");
        }
        finally
        {
            _seedStartInProgress = false;
            RefreshSeedUi();
        }
    }

    private async Task StopSeedAsync()
    {
        if (!_seed.IsRunning) return;
        BtnStop.IsEnabled = false;
        try
        {
            await _seed.StopAsync();
            AppendJournal("Набор выключен.");
        }
        finally
        {
            BtnStop.IsEnabled = true;
            RefreshSeedUi();
        }
    }

    private void RefreshSeedUi()
    {
        var s = _seed.State;

        BtnStart.Visibility = s.Phase is SeedPhase.Idle or SeedPhase.Completed
            ? Visibility.Visible : Visibility.Collapsed;
        BtnStop.Visibility = s.Phase is SeedPhase.Connecting or SeedPhase.OnTarget
            ? Visibility.Visible : Visibility.Collapsed;
        BtnStart.IsEnabled = _api.Token is not null && !_seedStartInProgress && _windowOpen != false;

        TxtSeedState.Text = s.StatusText;

        var (pillBg, pillFg, pillText) = s.Phase switch
        {
            SeedPhase.Connecting => ("PillBusyBg", "PillBusyFg", "ПОДКЛЮЧАЕМСЯ…"),
            SeedPhase.OnTarget => ("PillOkBg", "PillOkFg", "ИДЁТ СИД"),
            SeedPhase.Completed => ("PillOkBg", "PillOkFg", "ВСЕ СЕРВЕРЫ ЗАПОЛНЕНЫ"),
            _ => ("PillIdleBg", "PillIdleFg", "ВЫКЛЮЧЕНО"),
        };
        StatePill.Background = (Brush)FindResource(pillBg);
        TxtPill.Foreground = (Brush)FindResource(pillFg);
        TxtPill.Text = pillText;

        UpdateServersCard(); // бейдж «ВЫ ЗДЕСЬ» зависит от фазы
        TickUi();
    }

    // ─────────────────────────── Карточка серверов ───────────────────────────

    private sealed class ServerRow
    {
        public string FullName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public string Online { get; set; } = "";
        public string Hint { get; set; } = "";
        public string Badge { get; set; } = "";
        public bool IsTarget { get; set; }
        public bool IsOnline { get; set; } = true;
        public double Progress { get; set; }
        public double ProgressMax { get; set; } = 100;
        public Visibility TargetBadge => IsTarget ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Список всех серверов из публичного статуса: цель набора — первая и
    /// выделена; у каждой строки онлайн, карта, прогресс засева и кнопка
    /// быстрого подключения (работает и без включённого набора).
    /// </summary>
    private void UpdateServersCard()
    {
        var status = _lastStatus;
        var servers = status?.Servers ?? new List<ServerStatusInfo>();
        var targetKey = status?.Target?.Key;

        TxtTotalOnline.Text = servers.Count > 0
            ? $"всего онлайн: {servers.Where(s => IsOk(s)).Sum(s => s.Players)}"
            : "";
        TxtServersEmpty.Visibility = servers.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var rows = servers
            .OrderByDescending(s => s.Key == targetKey) // цель — первой
            .Select(s =>
            {
                var isTarget = s.Key == targetKey;
                var online = IsOk(s);
                var goal = _threshold > 0 ? _threshold : s.MaxPlayers;
                var left = Math.Max(0, goal - s.Players);
                return new ServerRow
                {
                    FullName = s.Name ?? "",
                    Title = ShortServerName(s.Name, s.Key),
                    Subtitle = string.Join(" · ", new[]
                        {
                            ServerModeTag(s.Name),
                            FormatMap(s.Map),
                        }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    Online = online ? $"{s.Players} / {s.MaxPlayers}" : "офлайн",
                    IsTarget = isTarget,
                    IsOnline = online,
                    Badge = isTarget && _seed.State.Phase == SeedPhase.OnTarget ? "ВЫ ЗДЕСЬ" : "ЦЕЛЬ НАБОРА",
                    Progress = online ? Math.Clamp(s.Players, 0, goal) : 0,
                    ProgressMax = Math.Max(1, goal),
                    Hint = !online
                        ? "сервер не отвечает"
                        : left > 0
                            ? $"до заполнения осталось {left} (порог {goal})"
                            : "заполнен",
                };
            })
            .ToList();
        ServersList.ItemsSource = rows;
    }

    private static bool IsOk(ServerStatusInfo s) =>
        string.Equals(s.Status, "ok", StringComparison.OrdinalIgnoreCase);

    private async void BtnConnectServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ServerRow row } btn) return;
        if (string.IsNullOrWhiteSpace(row.FullName)) return;
        btn.IsEnabled = false;
        try
        {
            var url = await _api.GetJoinUrlAsync(row.FullName, CancellationToken.None);
            if (url is null)
            {
                AppendJournal($"Не удалось получить ссылку подключения ({row.Title}).");
                return;
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppendJournal($"Подключение к {row.Title}: ссылка открыта в Steam.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppendJournal($"Подключение к {row.Title}: {ex.Message}");
        }
        finally
        {
            btn.IsEnabled = row.IsOnline;
        }
    }

    /// <summary>Короткое имя сервера: «Сервер №2» вместо полного рекламного названия.</summary>
    private static string ShortServerName(string? name, string? key)
    {
        var m = Regex.Match(name ?? "", @"#\s*(\d+)");
        if (m.Success) return $"Сервер №{m.Groups[1].Value}";
        if (!string.IsNullOrWhiteSpace(key)) return key!;
        return string.IsNullOrWhiteSpace(name) ? "Сервер" : name.Trim();
    }

    /// <summary>Режим из полного имени сервера: «#2 [RAAS/AAS] | …» → «RAAS/AAS».</summary>
    private static string ServerModeTag(string? name)
    {
        var m = Regex.Match(name ?? "", @"#\s*\d+\s*\[([^\]]+)\]");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Красивое имя карты: «Tallil_Outskirts_Invasion_v3» → «Tallil Outskirts · Invasion v3».</summary>
    private static string FormatMap(string? map)
    {
        if (string.IsNullOrWhiteSpace(map)) return "";
        var m = Regex.Match(map.Trim(), @"^(.+?)_([A-Za-z]+)_(v\d+)$");
        if (!m.Success) return map.Trim();
        return $"{m.Groups[1].Value.Replace('_', ' ')} · {m.Groups[2].Value} {m.Groups[3].Value}";
    }

    private void TickUi()
    {
        var session = _seed.State.Session;
        if (_seed.State.Phase == SeedPhase.OnTarget && session is not null)
        {
            var elapsed = DateTime.UtcNow - session.StartedAt.ToUniversalTime();
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            TxtTimer.Text = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            TxtTimer.Foreground = (Brush)FindResource("Text0");
            // Оценка бонусов: минуты × bonusDisplayRate (начисляет сервер); «+0» не показываем.
            var minutes = Math.Max(session.Minutes, (int)elapsed.TotalMinutes);
            var estimate = minutes * _seed.State.BonusRate;
            TxtBonuses.Text = estimate > 0 ? $"нафармлено ~+{estimate} бонусов" : "";
        }
        else if (_seed.State.Phase == SeedPhase.Connecting)
        {
            TxtTimer.Text = "—:—:—";
            TxtTimer.Foreground = (Brush)FindResource("TextDim");
            TxtBonuses.Text = "";
        }
        else
        {
            TxtTimer.Text = "00:00:00";
            TxtTimer.Foreground = (Brush)FindResource("TextDim"); // неактивный таймер — тусклый
            TxtBonuses.Text = "";
        }
    }

    // ─────────────────── Публичный статус серверов ───────────────────

    private async Task PublicStatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var status = await _api.GetStatusAsync(ct);
                if (status?.Ok == true)
                {
                    _windowOpen = status.Window?.Open; // null у старого бэкенда — не знаем
                    if (status.Threshold > 0) _threshold = status.Threshold;
                    _lastStatus = status;
                    var text = BuildStatusText(status);
                    Dispatcher.Invoke(() =>
                    {
                        TxtPublicStatus.Text = text;
                        UpdateServersCard();
                        RefreshSeedUi(); // доступность кнопки зависит от окна
                    });
                }
            }
            catch (ApiException ex)
            {
                LogService.Warn($"Публичный статус: {ex.Message}");
                var text = ex.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "Сервер обновлён: скачайте новую версию приложения"
                    : ex.IsAuthError ? "Ошибка авторизации"
                    : $"Сервер недоступен ({(int)ex.StatusCode})";
                Dispatcher.Invoke(() => TxtPublicStatus.Text = text);
            }
            catch (HttpRequestException ex)
            {
                LogService.Warn($"Публичный статус: {ex.Message}");
                Dispatcher.Invoke(() => TxtPublicStatus.Text = "Нет соединения с сервером");
            }
            catch (OperationCanceledException) { return; }

            // Баланс бонусов и VIP — тем же поллом, только когда залогинены
            if (_api.Token is not null)
            {
                try
                {
                    var vip = await _api.GetVipMyAsync(ct);
                    if (vip?.Ok == true)
                        Dispatcher.Invoke(() => UpdateVipUi(vip));
                }
                catch (ApiException ex) when (ex.IsAuthError)
                {
                    Dispatcher.Invoke(() =>
                    {
                        AppendJournal("Сессия истекла — войдите заново.");
                        ShowLoggedOut();
                    });
                }
                catch (Exception ex) when (ex is ApiException or HttpRequestException)
                {
                    LogService.Warn($"VIP-статус: {ex.Message}");
                }
                catch (OperationCanceledException) { return; }
            }

            try { await Task.Delay(PublicStatusInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ─────────────────────────── Автообновление ───────────────────────────

    private UpdateInfo? _pendingUpdate;
    private Version? _updateDismissed; // версия, для которой нажали «Позже» (на сессию)
    private bool _updateInProgress;

    private async Task UpdateCheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var current = GetType().Assembly.GetName().Version ?? new Version(1, 0, 0);
            var info = await UpdateService.CheckAsync(current, ct);
            if (info is not null)
            {
                _pendingUpdate = info;
                Dispatcher.Invoke(() =>
                {
                    if (_updateDismissed == info.Version || _updateInProgress) return;
                    TxtUpdate.Text = $"Доступна новая версия v{info.Version}";
                    UpdateBar.Visibility = Visibility.Visible;
                });
            }
            try { await Task.Delay(TimeSpan.FromHours(3), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (_updateInProgress || _pendingUpdate is not { } info) return;
        _updateInProgress = true;
        BtnUpdate.IsEnabled = false;
        BtnUpdateLater.IsEnabled = false;
        LogService.Info($"Update: старт обновления до v{info.Version}");
        AppendJournal($"Скачиваю обновление v{info.Version}…");
        // Прогресс скачивания — прямо в плашку: «Скачиваю обновление… 42%».
        var progress = new Progress<double>(p =>
        {
            TxtUpdate.Text = $"Скачиваю обновление v{info.Version} — {p * 100:0}%";
            BtnUpdate.Content = $"{p * 100:0}%";
        });
        TxtUpdate.Text = $"Скачиваю обновление v{info.Version}…";
        BtnUpdate.Content = "0%";
        try
        {
            await UpdateService.DownloadAndSwapAsync(info, CancellationToken.None, progress);
            TxtUpdate.Text = $"Обновление v{info.Version} установлено — перезапускаюсь…";
            LogService.Info("Update: всё готово, закрываю приложение для подмены exe");
            AppendJournal($"Обновление v{info.Version} готово — перезапускаюсь для установки.");
            await Task.Delay(800);
            _reallyClose = true;
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            LogService.Error("Автообновление не удалось", ex);
            AppendJournal($"Не удалось обновиться: {ex.Message}");
            TxtUpdate.Text = $"Не удалось скачать обновление v{info.Version} — попробуйте ещё раз";
            BtnUpdate.Content = "Обновить";
            BtnUpdate.IsEnabled = true;
            BtnUpdateLater.IsEnabled = true;
            _updateInProgress = false;
        }
    }

    // ─────────────────────────── VIP за бонусы ───────────────────────────

    private bool _vipBuyInProgress;
    private VipMyResponse? _lastVip;

    private void UpdateVipUi(VipMyResponse vip)
    {
        _lastVip = vip;
        TxtVipBalance.Text = $"У вас: {vip.Bonuses:N0} бонусов";
        TxtVipUntil.Text = vip.VipActive && vip.VipEndDate is { } until
            ? $"· VIP до {until.ToLocalTime():dd.MM.yyyy}"
            : "";
        TxtVipMissing.Text = vip.Missing > 0
            ? $"Не хватает {vip.Missing:N0} бонусов до VIP"
            : "Бонусов хватает на VIP!";
        BtnBuyVip.Content = $"Получить VIP за {vip.Price:N0}";
        BtnBuyVip.IsEnabled = vip.Missing <= 0 && !_vipBuyInProgress;
    }

    private async Task BuyVipAsync()
    {
        if (_vipBuyInProgress || _api.Token is null) return;
        var price = _lastVip?.Price ?? 15000;
        var days = _lastVip?.Days ?? 30;
        var confirm = ConfirmWindow.Ask(
            this,
            "Покупка VIP",
            $"Списать {price:N0} бонусов и продлить VIP на {days} дней?\nСрок суммируется с текущим.",
            "Получить VIP");
        if (!confirm) return;

        _vipBuyInProgress = true;
        BtnBuyVip.IsEnabled = false;
        try
        {
            await _api.BuyVipAsync(CancellationToken.None);
            AppendJournal("VIP оформлен за бонусы. Статус применится после смены карты.");
            var vip = await _api.GetVipMyAsync(CancellationToken.None);
            if (vip?.Ok == true) UpdateVipUi(vip);
        }
        catch (ApiException ex) when (ex.IsAuthError)
        {
            AppendJournal("Сессия истекла — войдите заново.");
            ShowLoggedOut();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            AppendJournal($"Не удалось оформить VIP: {ex.Message}");
        }
        finally
        {
            _vipBuyInProgress = false;
            BtnBuyVip.IsEnabled = true;
        }
    }

    private static string BuildStatusText(AutoseedStatusResponse status)
    {
        var servers = status.Servers ?? new List<ServerStatusInfo>();
        var online = servers.Count(s => string.Equals(s.Status, "ok", StringComparison.OrdinalIgnoreCase));
        var target = status.Target is { } t
            ? $"цель: {t.Name} ({t.Players}/{t.MaxPlayers})"
            : "цели нет — все серверы заполнены";
        var window = status.Window is { Open: false } w
            ? $"Набор начнётся {w.DescribeOpening()} · "
            : "";
        return $"{window}Серверов онлайн: {online} · порог {status.Threshold} · {target}";
    }

    // ─────────────────────────── Прочее UI ───────────────────────────

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings) { Owner = this };
        if (window.ShowDialog() == true)
        {
            _settings = window.Result;
            _settingsStore.Save(_settings);
            _api.BaseUrl = _settings.BaseUrl;
            NavJournal.Visibility = _settings.ShowJournal ? Visibility.Visible : Visibility.Collapsed;
            AppendJournal("Настройки сохранены.");
        }
    }

    private void AppendJournal(string line)
    {
        JournalList.Items.Add(line);
        while (JournalList.Items.Count > MaxJournalLines)
            JournalList.Items.RemoveAt(0);

        // Автоскролл откладываем за layout (в конструкторе шаблон ListBox ещё не
        // применён, а ScrollIntoView утаскивает и горизонтальный скролл вправо).
        Dispatcher.BeginInvoke(() =>
        {
            if (FindScrollViewer(JournalList) is { } sv)
            {
                sv.ScrollToEnd();
                sv.ScrollToHorizontalOffset(0);
            }
        }, DispatcherPriority.Background);
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            LogService.Warn($"Не удалось открыть {url}: {ex.Message}");
        }
    }

    // ─────────────────────────── Трей ───────────────────────────

    private void InitTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "RNS Companion",
            Icon = LogoService.CreateTrayIcon(),
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Открыть", null, (_, _) => Dispatcher.Invoke(ShowAndActivate));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitForReal));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);
    }

    private void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitForReal()
    {
        _reallyClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_reallyClose && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.ShowBalloonTip(2500, "RNS Companion",
                "Приложение продолжает работать в трее.", WinForms.ToolTipIcon.Info);
            return;
        }

        _statusPoll.Cancel();
        _seed.Shutdown();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnClosing(e);
        System.Windows.Application.Current.Shutdown();
    }
}
