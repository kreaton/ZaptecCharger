using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Zaptec.Api;

namespace ZaptecMonitor;

public partial class MainWindow : Window
{
    /// <summary>Don't repeat the same scheduled command more often than this if the charger ignores it.</summary>
    private static readonly TimeSpan ScheduleRetryDelay = TimeSpan.FromMinutes(2);

    private AppSettings _settings = new();
    private ChargeSchedule? _schedule;
    private ZaptecClient? _client;
    private ZaptecCharger? _charger;
    private ChargerState? _state;
    private bool _busy;

    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime? _nextPollAt;
    private DateTime _lastClockTick = DateTime.Now;

    private ScheduleAction _lastScheduleAction;
    private DateTime _lastScheduleActionAt;
    /// <summary>Set when the user presses Start/Pause by hand: the schedule stands back until its next boundary.</summary>
    private DateTime? _scheduleOverrideUntil;

    public MainWindow()
    {
        InitializeComponent();
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _clock.Tick += Clock_Tick;
        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += async (_, _) =>
        {
            _clock.Start();
            LoadSettings();
            await ConnectAsync();
            if (!_settings.Zaptec.HasCredentials)
                await OpenSettingsAsync();
        };
        Closed += (_, _) => { _clock.Stop(); _pollTimer.Stop(); _client?.Dispose(); };
    }

    // ---- Settings & connection ------------------------------------------------------------

    private void LoadSettings()
    {
        try
        {
            _settings = AppSettings.Load();
        }
        catch (Exception ex)
        {
            _settings = new AppSettings();
            ShowError("Could not read settings: " + ex.Message);
        }
        _schedule = ChargeSchedule.FromSettings(_settings.Schedule);
    }

    private async Task ConnectAsync()
    {
        _pollTimer.Stop();
        _nextPollAt = null;
        _client?.Dispose();
        _client = null;
        _charger = null;
        _state = null;
        _scheduleOverrideUntil = null;
        _lastScheduleAction = ScheduleAction.None;
        RenderEmpty();

        if (!_settings.Zaptec.HasCredentials)
        {
            ChargerName.Text = "Not signed in";
            StatusText.Text = "Welcome";
            SetFooter("Open settings (⚙) and enter your Zaptec portal login.");
            RefreshButton.IsEnabled = false;
            return;
        }

        _client = new ZaptecClient(_settings.Zaptec.Username, _settings.Zaptec.Password);
        ChargerName.Text = "Connecting…";
        RefreshButton.IsEnabled = true;
        await RefreshAsync();
    }

    private async Task OpenSettingsAsync()
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var old = _settings;
        _settings = dlg.Result;
        _schedule = ChargeSchedule.FromSettings(_settings.Schedule);

        var reconnect = old.Zaptec.Username != _settings.Zaptec.Username
                        || old.Zaptec.Password != _settings.Zaptec.Password
                        || old.Zaptec.ChargerId != _settings.Zaptec.ChargerId
                        || _client is null;

        if (reconnect)
        {
            await ConnectAsync();
        }
        else
        {
            // Only polling/schedule changed: re-evaluate right away with the new rules.
            _scheduleOverrideUntil = null;
            _lastScheduleAction = ScheduleAction.None;
            await RefreshAsync();
        }
    }

    // ---- Polling ---------------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_client is null || _busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        UpdatePollIndicator(DateTime.Now);
        try
        {
            var chargers = await _client.GetChargersAsync();
            if (_charger is null)
            {
                var id = _settings.Zaptec.ChargerId;
                _charger = string.IsNullOrWhiteSpace(id)
                    ? chargers.FirstOrDefault()
                    : chargers.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

                if (_charger is null)
                {
                    ChargerName.Text = "No charger";
                    ShowError(chargers.Count == 0
                        ? "No chargers found on this account."
                        : "The charger chosen in settings was not found on this account.");
                    return;
                }
            }
            else
            {
                _charger = chargers.FirstOrDefault(c => c.Id == _charger.Id) ?? _charger;
            }

            ChargerName.Text = _charger.Name;
            _state = await _client.GetStateAsync(_charger.Id);
            Render();
            SetFooter($"Updated {_state.RetrievedAt:HH:mm:ss}");

            await ApplyScheduleAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = _client is not null;
            RestartPollTimer();
            UpdatePollIndicator(DateTime.Now);
        }
    }

    private void RestartPollTimer()
    {
        _pollTimer.Stop();
        if (_client is null || !_settings.Zaptec.PollingEnabled)
        {
            _nextPollAt = null;
            return;
        }
        _pollTimer.Interval = TimeSpan.FromMinutes(_settings.Zaptec.PollIntervalMinutes);
        _pollTimer.Start();
        _nextPollAt = DateTime.Now + _pollTimer.Interval;
    }

    private void Clock_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;

        if (_scheduleOverrideUntil is { } until && now >= until)
            _scheduleOverrideUntil = null;

        // When the charging window opens or closes, check the charger straight away instead of
        // waiting for the next poll (this also covers the case where auto-refresh is off).
        if (_schedule is not null && _client is not null &&
            _schedule.IsAllowed(_lastClockTick) != _schedule.IsAllowed(now))
        {
            _ = RefreshAsync();
        }

        _lastClockTick = now;
        UpdatePollIndicator(now);
        UpdateScheduleText(now);
    }

    private void UpdatePollIndicator(DateTime now)
    {
        if (_busy)
        {
            PollText.Text = "Updating…";
            PollProgress.IsIndeterminate = true;
            PollProgress.Visibility = Visibility.Visible;
            return;
        }

        PollProgress.IsIndeterminate = false;

        if (_client is null)
        {
            PollText.Text = "";
            PollProgress.Visibility = Visibility.Hidden;
        }
        else if (!_settings.Zaptec.PollingEnabled || _nextPollAt is null)
        {
            PollText.Text = "Auto-refresh off";
            PollProgress.Visibility = Visibility.Hidden;
        }
        else
        {
            var remaining = _nextPollAt.Value - now;
            var secs = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
            var left = secs >= 60 ? $"{secs / 60}:{secs % 60:00} min" : $"{secs} s";
            PollText.Text = $"Next update {_nextPollAt.Value:HH:mm} (in {left})";
            var total = _pollTimer.Interval.TotalSeconds;
            PollProgress.Value = total <= 0 ? 0 : Math.Clamp(1 - remaining.TotalSeconds / total, 0, 1);
            PollProgress.Visibility = Visibility.Visible;
        }
    }

    // ---- Schedule --------------------------------------------------------------------------

    private async Task ApplyScheduleAsync()
    {
        if (_schedule is null || _client is null || _charger is null || _state is null || !_charger.IsOnline)
            return;

        var now = DateTime.Now;
        if (_scheduleOverrideUntil is not null) return;

        var action = _schedule.Decide(_state, now);
        if (action == ScheduleAction.None)
        {
            _lastScheduleAction = ScheduleAction.None;
            return;
        }

        // The charger hasn't reacted to the last identical command yet; don't hammer it.
        if (action == _lastScheduleAction && now - _lastScheduleActionAt < ScheduleRetryDelay)
            return;

        _lastScheduleAction = action;
        _lastScheduleActionAt = now;

        if (action == ScheduleAction.Start)
            await _client.StartChargingAsync(_charger.Id);
        else
            await _client.StopChargingAsync(_charger.Id);

        SetFooter(action == ScheduleAction.Start
            ? $"Schedule resumed charging ({_schedule})"
            : $"Schedule paused charging (window {_schedule})");

        // The charger applies the change asynchronously; give it a moment, then re-read.
        await Task.Delay(TimeSpan.FromSeconds(3));
        _state = await _client.GetStateAsync(_charger.Id);
        Render();
    }

    private void UpdateScheduleText(DateTime now)
    {
        if (!_settings.Schedule.Enabled)
        {
            ScheduleText.Text = "Schedule off";
            ScheduleDetail.Visibility = Visibility.Collapsed;
            return;
        }

        if (_schedule is null)
        {
            ScheduleText.Text = _settings.Schedule is { StartAtFrom: false, PauseAtTo: false }
                ? "Schedule on, but neither start nor pause is selected"
                : "Schedule has an invalid time — check settings";
            ScheduleDetail.Visibility = Visibility.Collapsed;
            return;
        }

        var allowed = _schedule.IsAllowed(now);
        var boundary = _schedule.NextBoundary(now);
        ScheduleText.Text = _schedule.From == _schedule.To
            ? "Charging allowed all day"
            : allowed
                ? $"Charging window {_schedule} · open until {boundary:HH:mm}"
                : $"Charging window {_schedule} · opens {boundary:HH:mm}";

        string detail;
        if (_scheduleOverrideUntil is { } until)
            detail = $"Manual override — schedule resumes at {until:HH:mm}";
        else if (_state is null || _state.Mode is ChargerOperationMode.Disconnected or ChargerOperationMode.Unknown)
            detail = "Takes effect when a car is connected";
        else if (allowed)
            detail = _schedule.StartAtFrom
                ? "Car connected — inside the window, charging allowed"
                : "Car connected — inside the window (auto-start is off)";
        else
            detail = _schedule.PauseAtTo
                ? "Car connected — charging held until the window opens"
                : "Car connected — outside the window (auto-pause is off)";

        var actions = (_schedule.StartAtFrom, _schedule.PauseAtTo) switch
        {
            (true, true) => $"Starts at {_schedule.From:HH\\:mm}, pauses at {_schedule.To:HH\\:mm}",
            (true, false) => $"Starts at {_schedule.From:HH\\:mm}",
            _ => $"Pauses at {_schedule.To:HH\\:mm}",
        };
        detail = actions + " · " + detail;

        ScheduleDetail.Text = detail;
        ScheduleDetail.Visibility = Visibility.Visible;
    }

    // ---- Rendering -------------------------------------------------------------------------

    private void Render()
    {
        if (_state is null || _charger is null) return;
        var s = _state;
        var online = _charger.IsOnline;

        StatusText.Text = s.Description;
        StatusText.Foreground = s.Mode == ChargerOperationMode.ConnectedCharging
            ? (Brush)FindResource("Accent")
            : Brushes.Black;
        PowerText.Text = $"{s.PowerWatts / 1000:0.00} kW";
        EnergyText.Text = $"{s.SessionEnergyKwh:0.00} kWh";
        OnlineDot.Fill = online ? (Brush)FindResource("Accent") : (Brush)FindResource("ErrorText");
        OnlineDot.ToolTip = online ? "Charger online" : "Charger offline";
        if (!online) StatusText.Text = "Charger offline";
        StartButton.IsEnabled = online && s.CanTryStart && !_busyCommand;
        StopButton.IsEnabled = online && s.CanStop && !_busyCommand;
        UpdateHint(s, online);
        UpdateScheduleText(DateTime.Now);
    }

    /// <summary>Explains in plain words why Start / Pause are or aren't available.</summary>
    private void UpdateHint(ChargerState s, bool online)
    {
        string hint, startTip, stopTip;
        const string StartRule = "Start (resume) only works on a session that was paused from this app or the API.";
        const string StopRule = "Pause only works while the car is actively charging.";

        if (!online)
        {
            hint = "The charger is offline, so no commands can be sent.";
            startTip = stopTip = hint;
        }
        else switch (s.Mode)
        {
            case ChargerOperationMode.Disconnected:
                hint = "Plug in a car to start or pause charging.";
                startTip = stopTip = "No car connected.";
                break;

            case ChargerOperationMode.ConnectedRequesting:
                hint = "The car is waiting for the charger to allow charging. Start works if a Zaptec schedule " +
                       "is holding it. If Zaptec refuses, the car's own timer or missing authorization is the " +
                       "likely cause; the footer shows Zaptec's reason." +
                       (s.NextScheduleEvent is { } next
                           ? $" Next Zaptec schedule change: {next.ToLocalTime():ddd HH:mm}."
                           : "");
                startTip = "Try to start now. Zaptec accepts this when its own schedule is holding the session.";
                stopTip = "Nothing to pause — the car isn't charging yet.";
                break;

            case ChargerOperationMode.ConnectedCharging:
                hint = "Charging. Pause stops it, and you can resume it here afterwards.";
                startTip = "Already charging.";
                stopTip = "Pause charging. You can resume it with Start.";
                break;

            case ChargerOperationMode.ConnectedFinished when s.FinalStopActive:
                hint = "Paused from this app or the API. Press Start to resume.";
                startTip = "Resume charging.";
                stopTip = "Already paused.";
                break;

            case ChargerOperationMode.ConnectedFinished:
                hint = "The car stopped drawing power (it's full, or its own timer stopped it). " +
                       "Zaptec can't restart that session from the API; unplug and replug, or start it from the car.";
                startTip = "Can't start: the car ended the session itself. " + StartRule;
                stopTip = "Nothing to pause. " + StopRule;
                break;

            default:
                hint = "The charger reports an unknown state.";
                startTip = StartRule;
                stopTip = StopRule;
                break;
        }

        HintText.Text = hint;
        HintIcon.Visibility = Visibility.Visible;
        StartButton.ToolTip = startTip;
        StopButton.ToolTip = stopTip;
    }

    private void RenderEmpty()
    {
        StatusText.Text = "–";
        StatusText.Foreground = Brushes.Black;
        PowerText.Text = "–";
        EnergyText.Text = "–";
        OnlineDot.Fill = Brushes.LightGray;
        OnlineDot.ToolTip = null;
        StartButton.IsEnabled = StopButton.IsEnabled = false;
        StartButton.ToolTip = StopButton.ToolTip = null;
        HintText.Text = "";
        HintIcon.Visibility = Visibility.Collapsed;
        UpdateScheduleText(DateTime.Now);
    }

    private void SetFooter(string text)
    {
        FooterText.Text = text;
        FooterText.Foreground = (Brush)FindResource("MutedText");
    }

    private void ShowError(string message)
    {
        FooterText.Text = message;
        FooterText.Foreground = (Brush)FindResource("ErrorText");
        if (_state is null) StatusText.Text = "Error";
    }

    // ---- Commands & UI events --------------------------------------------------------------

    private bool _busyCommand;

    private async void StartButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(c => c.StartChargingAsync(_charger!.Id));

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(c => c.StopChargingAsync(_charger!.Id));

    private async Task RunCommandAsync(Func<ZaptecClient, Task> command)
    {
        if (_client is null || _charger is null || _busy || _busyCommand) return;

        // A manual press wins over the schedule until the window next opens or closes.
        if (_schedule is not null)
            _scheduleOverrideUntil = _schedule.NextBoundary(DateTime.Now);

        _busyCommand = true;
        StartButton.IsEnabled = StopButton.IsEnabled = false;
        SetFooter("Sending command…");
        try
        {
            await command(_client);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _busyCommand = false;
            ShowError(ex.Message);
            Render(); // re-enable the buttons that still make sense
            return;
        }
        _busyCommand = false;
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void SettingsButton_Click(object sender, RoutedEventArgs e) => await OpenSettingsAsync();

    private HistoryWindow? _history;

    private void HistoryButton_Click(object sender, RoutedEventArgs e) => OpenHistory();

    /// <summary>Opens the history window, or brings the open one to the front.</summary>
    private void OpenHistory()
    {
        if (_history is { IsLoaded: true })
        {
            if (_history.WindowState == WindowState.Minimized) _history.WindowState = WindowState.Normal;
            _history.Activate();
            return;
        }
        _history = new HistoryWindow(() => (_client, _charger), () => _settings.Price) { Owner = this };
        _history.Closed += (_, _) => _history = null;
        _history.Show();
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            await RefreshAsync();
        }
        else if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenHistory();
        }
        else if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await OpenSettingsAsync();
        }
    }
}
