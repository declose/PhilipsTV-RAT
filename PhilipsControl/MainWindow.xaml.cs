using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PhilipsControl.Models;
using PhilipsControl.Services;

namespace PhilipsControl;

public partial class MainWindow : Window
{
    private static readonly string[] Pages = ["overview", "remote", "media", "apps", "ambilight", "log", "settings"];
    private static readonly HashSet<string> StateChangingKeys = ["Standby", "Home", "Source", "PlayPause", "Play", "Pause", "Stop", "Mute", "VolumeUp", "VolumeDown",
        "ChannelStepUp", "ChannelStepDown", "WatchTV", "Back", "Confirm", "AmbilightOnOff", "Digit0", "Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "Digit8", "Digit9"];

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly TvStore _store = new();
    private readonly PhilipsDiscovery _discovery = new();
    private readonly ObservableCollection<TvRecord> _savedTvs = [];
    private readonly ObservableCollection<DiscoveryResult> _foundTvs = [];
    private readonly ObservableCollection<TvApplication> _apps = [];
    private readonly ObservableCollection<LogEntry> _log = [];
    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _quickRefresh = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TrayIcon? _tray;
    private TvRecord _active = new() { Name = "No TV connected", StatusMessage = "Add a TV to get started.", LastSeen = "—" };
    private PhilipsJointSpaceClient? _client;
    private CancellationTokenSource? _connectionCancellation;
    private string _currentPage = "";
    private string _bannerAction = "";
    private bool _discovering;
    private bool _connecting;
    private bool _refreshInProgress;
    private bool _refreshQueued;
    private bool _needsReconnect;
    private bool _reallyClosing;
    private int _connectionId;
    private int _refreshId;
    private int _pollTick;

    public MainWindow(bool startMinimized = false)
    {
        InitializeComponent();
        RestoreWindowBounds();
        ApplyAccent(_settings.Accent);
        DataContext = _active;
        SidebarTvList.ItemsSource = _savedTvs;
        SavedTVList.ItemsSource = _savedTvs;
        FoundTVList.ItemsSource = _foundTvs;
        LogList.ItemsSource = _log;
        InitializeFeatures();
        InitializePreferences();

        _pollTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_settings.PollSeconds, 2, 60));
        _pollTimer.Tick += async (_, _) => await PollTickAsync();
        _quickRefresh.Tick += async (_, _) => { _quickRefresh.Stop(); await RefreshSnapshotAsync(includeApplications: false); };
        _clockTimer.Tick += (_, _) => { TitleCursor.Opacity = TitleCursor.Opacity > 0 ? 0 : 1; SleepTick(); };
        _clockTimer.Start();

        try
        {
            _tray = new TrayIcon(BringToFront, key => _ = RunRemoteKeyAsync(key), () => _ = TogglePowerAsync(), SetSleepTimer, ExitApp);
        }
        catch (Exception ex) { Log("Tray icon unavailable: " + ex.Message, LogKind.Warning); }

        SourceInitialized += OnSourceInitialized;
        StateChanged += (_, _) => MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        Closing += OnClosing;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
        _savedTvs.CollectionChanged += (_, _) => UpdateEmptyState();

        // Startup work runs from the dispatcher rather than Loaded, so it also happens when launched hidden in the tray.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(async () => await StartupAsync(startMinimized)));
    }

    private async Task StartupAsync(bool startMinimized)
    {
        Log($"Philips Control {VersionLabel} started.", LogKind.Info);
        foreach (var record in _store.Load()) _savedTvs.Add(record);
        UpdateEmptyState();
        ShowPage(Pages.Contains(_settings.LastPage) ? _settings.LastPage : "overview");
        UpdateStatusUi();
        if (startMinimized && _settings.CloseToTray && !_settings.TrayHintShown) _tray?.Balloon("Philips Control", "Running in the notification area.");
        var initial = _savedTvs.FirstOrDefault(tv => tv.Host.Equals(_settings.ActiveHost, StringComparison.OrdinalIgnoreCase)) ?? _savedTvs.FirstOrDefault();
        if (initial is not null) await ConnectRecordAsync(initial, false);
        else
        {
            SetHeaderStatus("Scanning for Philips TVs...", false);
            ShowPage("settings");
            await ScanForTvsAsync();
        }
    }

    private static string VersionLabel => "v" + (typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "2.0.0");

    // ───────────────────────────── Navigation ─────────────────────────────

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { CommandParameter: string page }) ShowPage(page);
    }

    private void GoToPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page }) ShowPage(page);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => ShowPage("settings");

    private void ShowPage(string page)
    {
        _currentPage = page;
        _settings.LastPage = page;
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        RemotePage.Visibility = page == "remote" ? Visibility.Visible : Visibility.Collapsed;
        MediaPage.Visibility = page == "media" ? Visibility.Visible : Visibility.Collapsed;
        AppsPage.Visibility = page == "apps" ? Visibility.Visible : Visibility.Collapsed;
        AmbilightPage.Visibility = page == "ambilight" ? Visibility.Visible : Visibility.Collapsed;
        LogPage.Visibility = page == "log" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        var nav = page switch
        {
            "remote" => NavRemote,
            "media" => NavMedia,
            "apps" => NavApps,
            "ambilight" => NavAmbilight,
            "log" => NavLog,
            "settings" => NavSettings,
            _ => NavOverview
        };
        if (nav.IsChecked != true) nav.IsChecked = true;
        PageHeading.Text = nav.Content as string ?? "Overview";
        PageEyebrow.Text = "PHILIPS TV / " + PageHeading.Text.ToUpperInvariant();
        if (page == "media") EnsureChannelsLoaded();
        if (page == "log") ScrollLogToEnd();
    }

    // ───────────────────────────── Connection ─────────────────────────────

    private async Task ConnectRecordAsync(TvRecord record, bool announce)
    {
        var connectionId = ++_connectionId;
        _refreshId++;
        _pollTimer.Stop();
        _quickRefresh.Stop();
        _connectionCancellation?.Cancel();
        _connectionCancellation?.Dispose();
        _connectionCancellation = new CancellationTokenSource();
        var connectionToken = _connectionCancellation.Token;
        _client?.Dispose();
        _client = null;
        _refreshInProgress = false;
        _needsReconnect = false;
        _connecting = true;
        SetActive(record);
        _settings.ActiveHost = record.Host;
        _apps.Clear();
        ResetMediaState();
        record.IsOnline = false;
        record.PowerStateKnown = false;
        record.AuthFailed = false;
        record.StatusMessage = "Connecting to " + record.Host + "...";
        SetHeaderStatus("Connecting...", false);
        Log($"Connecting to {record.DisplayName} at {record.Host} ({record.ApiLabel})...", LogKind.Info);
        try
        {
            _client = new PhilipsJointSpaceClient(record);
            if (record.ApiVersion.StartsWith('6') && record.UseHttps && string.IsNullOrWhiteSpace(record.Username))
            {
                Log("This TV needs pairing. Waiting for the PIN shown on screen.", LogKind.Info);
                var credentials = await _client.PairAsync(() => Task.FromResult(TerminalDialog.AskPin(this, record.DisplayName)), connectionToken);
                if (connectionId != _connectionId) return;
                record.Username = credentials.Username;
                record.Password = credentials.Password;
                SaveDevices();
                _client.Dispose();
                _client = new PhilipsJointSpaceClient(record);
                Log("Pairing complete. Credentials stored with Windows DPAPI.", LogKind.Success);
                Toast("Paired with " + record.DisplayName, LogKind.Success);
            }
            await _client.RefreshSnapshotAsync(_apps, connectionToken);
            if (connectionId != _connectionId) return;
            _connecting = false;
            if (record.AuthFailed)
            {
                Log("The TV rejected the stored pairing.", LogKind.Warning);
                if (announce) Toast("Pairing expired. Use PAIR AGAIN.", LogKind.Warning);
            }
            else if (!record.IsOnline)
            {
                record.StatusMessage = "The TV is not responding. It will be picked up automatically when it comes online.";
                Log($"{record.DisplayName} is not responding. Watching for it to come online.", LogKind.Warning);
                if (announce) Toast(record.DisplayName + " is not responding", LogKind.Warning);
            }
            else
            {
                Log($"Connected to {record.DisplayName} · {record.ModelLabel}.", LogKind.Success);
                if (announce)
                {
                    DiscoveryStatus.Text = $"Connected to {record.DisplayName}.";
                    Toast("Connected to " + record.DisplayName, LogKind.Success);
                }
                await AfterConnectedAsync(record, connectionId);
            }
            SaveDevices();
            _pollTimer.Start();
        }
        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested || connectionId != _connectionId)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Must come before the OperationCanceledException handler: HTTP timeouts are TaskCanceledExceptions.
            if (connectionId != _connectionId) return;
            // The TV is off or unreachable: keep retrying quietly instead of giving up until the user clicks again.
            record.StatusMessage = "The TV is not reachable. Retrying automatically.";
            Log($"{record.DisplayName} is not reachable ({ShortError(ex)}). Retrying in the background.", LogKind.Warning);
            if (announce) Toast(record.DisplayName + " is not reachable", LogKind.Warning);
            _needsReconnect = true;
            _pollTimer.Start();
        }
        catch (OperationCanceledException)
        {
            if (connectionId != _connectionId) return;
            record.StatusMessage = "Pairing was cancelled. Use CONNECT to try again.";
            Log("Pairing cancelled.", LogKind.Warning);
            if (announce) DiscoveryStatus.Text = record.StatusMessage;
        }
        catch (Exception ex)
        {
            if (connectionId != _connectionId) return;
            record.StatusMessage = ex.Message;
            Log("Connection failed: " + ex.Message, LogKind.Error);
            if (announce) TerminalDialog.Inform(this, "Connect to Philips TV", ex.Message);
        }
        finally
        {
            if (connectionId == _connectionId)
            {
                _connecting = false;
                UpdateStatusUi();
                UpdateEmptyState();
            }
        }
    }

    private async Task AfterConnectedAsync(TvRecord record, int connectionId)
    {
        if (string.IsNullOrWhiteSpace(record.MacAddress))
        {
            var mac = await WakeOnLan.ResolveMacAsync(record.Host);
            if (connectionId != _connectionId) return;
            if (mac.Length > 0)
            {
                record.MacAddress = mac;
                MacInput.Text = mac;
                Log("Wake-on-LAN address detected: " + mac, LogKind.Info);
            }
        }
        await LoadSourcesAsync(connectionId);
    }

    private void SetActive(TvRecord record)
    {
        if (!ReferenceEquals(_active, record))
        {
            _active.PropertyChanged -= Active_PropertyChanged;
            _active.IsActive = false;
        }
        _active = record;
        _active.IsActive = true;
        _active.PropertyChanged -= Active_PropertyChanged;
        _active.PropertyChanged += Active_PropertyChanged;
        DataContext = _active;
        MacInput.Text = record.MacAddress;
        SyncVolumeSlider();
        UpdateAmbilightSelection();
        UpdateStatusUi();
    }

    private void Active_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TvRecord.IsOnline) or nameof(TvRecord.IsPoweredOn) or nameof(TvRecord.PowerStateKnown) or nameof(TvRecord.AuthFailed) or nameof(TvRecord.Name) or nameof(TvRecord.Alias):
                UpdateStatusUi(); break;
            case nameof(TvRecord.Volume) or nameof(TvRecord.MaxVolume):
                SyncVolumeSlider(); break;
            case nameof(TvRecord.AmbilightStyle):
                UpdateAmbilightSelection(); break;
            case nameof(TvRecord.CurrentChannel):
                MarkCurrentChannel(); break;
        }
    }

    private async Task PollTickAsync()
    {
        _pollTick++;
        if (_needsReconnect)
        {
            // Retry roughly every 15 seconds without hammering an unreachable host.
            if (_pollTick % Math.Max(1, 15 / Math.Max(1, _settings.PollSeconds)) == 0 && !_connecting)
                await ConnectRecordAsync(_active, false);
            return;
        }
        // Refresh the app list about once a minute, or right away when it is missing.
        var includeApps = _apps.Count == 0 || _pollTick % Math.Max(1, 60 / Math.Max(1, _settings.PollSeconds)) == 0;
        await RefreshSnapshotAsync(includeApps);
    }

    private void QueueRefresh()
    {
        _quickRefresh.Stop();
        _quickRefresh.Start();
    }

    private async Task RefreshSnapshotAsync(bool includeApplications = true)
    {
        if (_client is null || _active.Host.Length == 0 || _connecting) return;
        if (_refreshInProgress) { _refreshQueued = true; return; }
        var client = _client;
        var record = _active;
        var cancellationToken = _connectionCancellation?.Token ?? CancellationToken.None;
        var refreshId = ++_refreshId;
        var wasOnline = record.IsOnline;
        var wasAuthFailed = record.AuthFailed;
        var heldVolume = VolumeHeld ? (int?)Math.Round(VolumeSlider.Value) : null;
        _refreshInProgress = true;
        try
        {
            await client.RefreshSnapshotAsync(_apps, cancellationToken, includeApplications: includeApplications || (!wasOnline && _apps.Count == 0));
            if (!ReferenceEquals(client, _client) || !ReferenceEquals(record, _active)) return;
            if (heldVolume is { } held && VolumeHeld) record.Volume = held;
            if (record.IsOnline && !wasOnline)
            {
                Log($"{record.DisplayName} is online.", LogKind.Success);
                if (_apps.Count == 0) QueueRefreshWithApps();
                _ = AfterConnectedAsync(record, _connectionId);
            }
            else if (!record.IsOnline && wasOnline) Log($"{record.DisplayName} stopped responding.", LogKind.Warning);
            if (record.AuthFailed && !wasAuthFailed) Log("The TV no longer accepts this PC's pairing. Use PAIR AGAIN.", LogKind.Error);
            if (record.IsOnline) SaveDevices();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(client, _client) || !ReferenceEquals(record, _active)) return;
            record.IsOnline = false;
            record.StatusMessage = "The TV is not responding. The connection will be checked again.";
            Log("Refresh failed: " + ex.Message, LogKind.Warning);
        }
        finally
        {
            if (refreshId == _refreshId) _refreshInProgress = false;
            UpdateStatusUi();
            UpdateAppsUi();
            if (_refreshQueued && !_refreshInProgress)
            {
                _refreshQueued = false;
                QueueRefresh();
            }
        }
    }

    private async void QueueRefreshWithApps()
    {
        await Task.Delay(300);
        await RefreshSnapshotAsync(includeApplications: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_needsReconnect && _active.Host.Length > 0) { await ConnectRecordAsync(_active, true); return; }
        if (_client is null) { ShowConnectNeeded(); return; }
        await RefreshSnapshotAsync();
        Toast(_active.IsOnline ? "Status refreshed" : "The TV is not responding", _active.IsOnline ? LogKind.Info : LogKind.Warning);
    }

    // ───────────────────────────── Status UI ─────────────────────────────

    private void SetHeaderStatus(string message, bool online)
    {
        HeaderStatus.Text = message;
        HeaderOnlineDot.Fill = online ? (Brush)FindResource("Accent") : (Brush)FindResource("Faint");
    }

    private void UpdateStatusUi()
    {
        var tv = _active;
        var hasTv = tv.Host.Length > 0;
        if (!hasTv) SetHeaderStatus(_discovering ? "Scanning for Philips TVs..." : "No TV connected", false);
        else if (_connecting) SetHeaderStatus("Connecting to " + tv.Host + "...", false);
        else if (tv.AuthFailed) SetHeaderStatus("Pairing required / " + tv.Host, false);
        else if (tv.IsOnline && tv.PowerStateKnown && !tv.IsPoweredOn) SetHeaderStatus("Standby / " + tv.Host, false);
        else if (tv.IsOnline) SetHeaderStatus("Online / " + tv.Host, true);
        else SetHeaderStatus("Offline / " + tv.Host, false);
        if (tv.IsOnline && tv.PowerStateKnown && !tv.IsPoweredOn) HeaderOnlineDot.Fill = (Brush)FindResource("Warn");

        PowerButtonText.Text = tv.IsActiveOn ? "POWER OFF" : "POWER ON";

        // Contextual banner on the Overview page.
        if (!hasTv || _connecting)
            ShowBanner(null, "", "", "");
        else if (tv.AuthFailed)
            ShowBanner("PAIRING EXPIRED", "The TV no longer accepts this PC. This happens after a TV reset or software update. Pair again to restore control.", "[ PAIR AGAIN ]", "repair");
        else if (!tv.IsOnline)
            ShowBanner("TV OFFLINE", string.IsNullOrWhiteSpace(tv.MacAddress)
                    ? "The TV is off or unreachable. Add its MAC address in Settings to switch it on with Wake-on-LAN."
                    : "The TV is off or unreachable. Send a Wake-on-LAN packet to switch it on.",
                string.IsNullOrWhiteSpace(tv.MacAddress) ? "[ SETTINGS ]" : "[ WAKE TV ]", string.IsNullOrWhiteSpace(tv.MacAddress) ? "settings" : "power");
        else if (tv.PowerStateKnown && !tv.IsPoweredOn)
            ShowBanner("TV IN STANDBY", "The TV is reachable but in standby.", "[ POWER ON ]", "power");
        else
            ShowBanner(null, "", "", "");

        _tray?.Update(hasTv ? tv.DisplayName : "No TV connected", hasTv ? tv.StateBadge.ToLowerInvariant() : "not set up", tv.IsActiveOn);
    }

    private void ShowBanner(string? title, string text, string action, string actionKey)
    {
        Banner.Visibility = title is null ? Visibility.Collapsed : Visibility.Visible;
        if (title is null) return;
        BannerTitle.Text = title;
        BannerText.Text = text;
        BannerAction.Content = action;
        _bannerAction = actionKey;
    }

    private async void BannerAction_Click(object sender, RoutedEventArgs e)
    {
        switch (_bannerAction)
        {
            case "repair": await RepairAsync(); break;
            case "settings": ShowPage("settings"); break;
            case "power": await TogglePowerAsync(); break;
        }
    }

    private void UpdateEmptyState()
    {
        NoSavedText.Visibility = _savedTvs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────────── Remote keys & power ─────────────────────────────

    private async void RemoteKey_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key) await RunRemoteKeyAsync(key);
    }

    private async Task<bool> RunRemoteKeyAsync(string key)
    {
        if (_client is null || _active.Host.Length == 0) { ShowConnectNeeded(); return false; }
        var client = _client;
        var record = _active;
        // Optimistic feedback so volume and mute feel instant; the follow-up refresh corrects it.
        switch (key)
        {
            case "VolumeUp": record.Volume = Math.Min(record.MaxVolume, record.Volume + 1); record.IsMuted = false; break;
            case "VolumeDown": record.Volume = Math.Max(0, record.Volume - 1); break;
            case "Mute": record.IsMuted = !record.IsMuted; break;
        }
        try
        {
            await client.SendKeyAsync(key);
            Log("KEY " + key, LogKind.Command);
            if (!ReferenceEquals(client, _client) || !ReferenceEquals(record, _active)) return false;
            if (StateChangingKeys.Contains(key)) QueueRefresh();
            return true;
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(client, _client) || !ReferenceEquals(record, _active)) return false;
            Log($"KEY {key} failed: {ex.Message}", LogKind.Error);
            Toast(record.AuthFailed ? "Pairing expired. Use PAIR AGAIN." : $"{key}: {ShortError(ex)}", LogKind.Error);
            if (StateChangingKeys.Contains(key)) QueueRefresh();
            return false;
        }
    }

    private async void PowerButton_Click(object sender, RoutedEventArgs e) => await TogglePowerAsync();

    private async Task TogglePowerAsync()
    {
        var target = _active;
        var connectionId = _connectionId;
        if (target.Host.Length == 0) { ShowPage("settings"); return; }
        try
        {
            if (target.IsOnline && _client is not null && target.IsActiveOn)
            {
                await _client.SetPowerAsync(false);
                if (!ReferenceEquals(target, _active)) return;
                target.IsPoweredOn = false;
                target.PowerStateKnown = true;
                Log("Power: standby.", LogKind.Command);
                Toast(target.DisplayName + " is switching to standby", LogKind.Info);
                QueueRefresh();
                return;
            }
            if (target.IsOnline && _client is not null)
            {
                // Android TVs in network standby still answer the API and wake through it.
                Log("Power: on (network standby).", LogKind.Command);
                Toast("Waking " + target.DisplayName + "...", LogKind.Info);
                var viaApi = _client.SetPowerAsync(true);
                if (!string.IsNullOrWhiteSpace(target.MacAddress)) await WakeOnLan.SendAsync(target.MacAddress, target.Host);
                await viaApi;
                await Task.Delay(1500);
                if (ReferenceEquals(target, _active)) await RefreshSnapshotAsync(false);
                return;
            }
            if (string.IsNullOrWhiteSpace(target.MacAddress)) target.MacAddress = await WakeOnLan.ResolveMacAsync(target.Host);
            if (string.IsNullOrWhiteSpace(target.MacAddress))
            {
                TerminalDialog.Inform(this, "Power on unavailable", "The TV's MAC address is required to wake it from standby. Enter it under Settings › Active TV / Wake-on-LAN, and enable Wake-on-LAN in the TV's network settings.");
                ShowPage("settings");
                return;
            }
            await WakeOnLan.SendAsync(target.MacAddress, target.Host);
            if (!ReferenceEquals(target, _active) || connectionId != _connectionId) return;
            Log($"Wake-on-LAN sent to {target.MacAddress}.", LogKind.Command);
            Toast("Wake-on-LAN sent. Waiting for the TV...", LogKind.Info);
            SetHeaderStatus("Waking TV...", false);
            await WaitForTvAsync(target, connectionId);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(target, _active)) return;
            Log("Power command failed: " + ex.Message, LogKind.Error);
            Toast("Power: " + ShortError(ex), LogKind.Error);
        }
    }

    /// <summary>A TV needs 5-20 seconds to boot its network stack, so poll instead of a single fixed delay.</summary>
    private async Task WaitForTvAsync(TvRecord target, int connectionId)
    {
        using var probe = new PhilipsJointSpaceClient(target);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(2500);
            if (!ReferenceEquals(target, _active) || connectionId != _connectionId) return;
            if (attempt == 3 && !string.IsNullOrWhiteSpace(target.MacAddress)) await WakeOnLan.SendAsync(target.MacAddress, target.Host);
            if (await probe.CheckOnlineAsync())
            {
                Log(target.DisplayName + " woke up.", LogKind.Success);
                await ConnectRecordAsync(target, false);
                return;
            }
        }
        if (ReferenceEquals(target, _active))
        {
            Log("The TV did not respond after Wake-on-LAN.", LogKind.Warning);
            Toast("No response. Check that Wake-on-LAN is enabled on the TV.", LogKind.Warning);
            UpdateStatusUi();
        }
    }

    // ───────────────────────────── Keyboard control ─────────────────────────────

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || e.IsRepeat && e.Key is not (Key.Add or Key.Subtract or Key.OemPlus or Key.OemMinus or Key.Up or Key.Down or Key.Left or Key.Right)) return;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.Control && e.Key is >= Key.D1 and <= Key.D7)
        {
            ShowPage(Pages[e.Key - Key.D1]);
            e.Handled = true;
            return;
        }
        if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift) return;
        if (e.Key == Key.F5) { e.Handled = true; await RefreshSnapshotAsync(); return; }
        if (!_settings.KeyboardShortcuts) return;
        string? key = e.Key switch
        {
            Key.Up => "CursorUp",
            Key.Down => "CursorDown",
            Key.Left => "CursorLeft",
            Key.Right => "CursorRight",
            Key.Enter => "Confirm",
            Key.Back or Key.Escape => "Back",
            Key.H => "Home",
            Key.Space => "PlayPause",
            Key.Add or Key.OemPlus => "VolumeUp",
            Key.Subtract or Key.OemMinus => "VolumeDown",
            Key.M => "Mute",
            Key.PageUp => "ChannelStepUp",
            Key.PageDown => "ChannelStepDown",
            Key.S => "Source",
            Key.I => "Info",
            Key.O => "Options",
            Key.A => "AmbilightOnOff",
            >= Key.D0 and <= Key.D9 => "Digit" + (e.Key - Key.D0),
            >= Key.NumPad0 and <= Key.NumPad9 => "Digit" + (e.Key - Key.NumPad0),
            _ => null
        };
        if (e.Key == Key.P) { e.Handled = true; await TogglePowerAsync(); return; }
        if (key is null) return;
        // Sliders keep their own arrow keys; everything else goes to the TV.
        if (Keyboard.FocusedElement is Slider && e.Key is Key.Left or Key.Right or Key.Up or Key.Down) return;
        e.Handled = true;
        await RunRemoteKeyAsync(key);
    }

    // ───────────────────────────── Log & toasts ─────────────────────────────

    private void Log(string text, LogKind kind)
    {
        _log.Add(new LogEntry { Text = text, Kind = kind });
        while (_log.Count > 500) _log.RemoveAt(0);
        StatusKind.Text = kind switch { LogKind.Success => "OK", LogKind.Warning => "WARN", LogKind.Error => "ERROR", LogKind.Command => "SENT", _ => "INFO" };
        StatusKind.Foreground = KindBrush(kind);
        StatusText.Text = text;
        if (_currentPage == "log") ScrollLogToEnd();
    }

    private static Brush KindBrush(LogKind kind) =>
        (Brush)new LogKindBrushConverter().Convert(kind, typeof(Brush), null!, System.Globalization.CultureInfo.InvariantCulture);

    private void ScrollLogToEnd()
    {
        Dispatcher.InvokeAsync(() =>
        {
            LogList.ApplyTemplate();
            (LogList.Template?.FindName("LogScroll", LogList) as ScrollViewer)?.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _log.Select(l => $"{l.Time:yyyy-MM-dd HH:mm:ss}  {l.KindLabel}  {l.Text}")));
            Toast("Console copied to the clipboard", LogKind.Success);
        }
        catch (Exception ex) { Toast("Clipboard unavailable: " + ex.Message, LogKind.Error); }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void Toast(string text, LogKind kind)
    {
        if (!_settings.Notifications && kind != LogKind.Error) return;
        if (!IsVisible) return;
        var brush = KindBrush(kind);
        var glyph = kind switch { LogKind.Success => "", LogKind.Warning => "", LogKind.Error => "", _ => "" };
        var toast = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(14, 19, 14)),
            BorderBrush = brush,
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 8, 0, 0),
            Opacity = 0,
            Cursor = Cursors.Hand,
            RenderTransform = new TranslateTransform(0, 10),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.55, Color = Colors.Black },
            Child = new DockPanel
            {
                Children =
                {
                    new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), Foreground = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        void Remove()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) => ToastHost.Children.Remove(toast);
            toast.BeginAnimation(OpacityProperty, fade);
        }
        toast.MouseLeftButtonUp += (_, _) => Remove();
        while (ToastHost.Children.Count >= 4) ToastHost.Children.RemoveAt(0);
        ToastHost.Children.Add(toast);
        toast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        ((TranslateTransform)toast.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase() });
        var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == LogKind.Error ? 5 : 3.2) };
        life.Tick += (_, _) => { life.Stop(); Remove(); };
        life.Start();
    }

    private static string ShortError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"the TV answered HTTP {(int)code}",
        HttpRequestException => "the TV is not reachable",
        TaskCanceledException => "the TV did not answer in time",
        _ => ex.Message
    };

    private void ShowConnectNeeded()
    {
        Toast(_active.Host.Length == 0 ? "Add a TV first (Settings)" : "Not connected to the TV yet", LogKind.Warning);
    }

    public void ReportUnhandled(Exception ex)
    {
        Log("Unexpected error: " + ex.Message, LogKind.Error);
        Toast("Unexpected error: " + ex.Message, LogKind.Error);
    }

    // ───────────────────────────── Window chrome & lifetime ─────────────────────────────

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowBounds();
        if (_reallyClosing || !_settings.CloseToTray || _tray is null) return;
        e.Cancel = true;
        Hide();
        if (!_settings.TrayHintShown)
        {
            _settings.TrayHintShown = true;
            _settings.Save();
            _tray.Balloon("Philips Control is still running", "Use the tray icon for quick controls, or right-click it and choose Exit.");
        }
    }

    private void ExitApp()
    {
        _reallyClosing = true;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _settings.Save();
        _pollTimer.Stop();
        _clockTimer.Stop();
        _quickRefresh.Stop();
        _connectionId++;
        _connectionCancellation?.Cancel();
        _connectionCancellation?.Dispose();
        _client?.Dispose();
        _tray?.Dispose();
        Application.Current.Shutdown();
    }

    private void RestoreWindowBounds()
    {
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);
        var left = _settings.WindowLeft;
        var top = _settings.WindowTop;
        // Only reuse the saved position if it is still on a connected screen.
        if (!double.IsNaN(left) && !double.IsNaN(top) &&
            left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 10 &&
            left + 200 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || double.IsInfinity(bounds.Width)) return;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        // Windows 11: rounded corners and a border that matches the theme. Ignored on Windows 10.
        var round = 2;
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        var border = 0x00293326;
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }

    /// <summary>Keeps the borderless window inside the monitor work area when maximized, so it never covers the taskbar.</summary>
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmGetMinMaxInfo = 0x0024;
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        mmi.MaxSize.X = info.Work.Right - info.Work.Left;
        mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        Marshal.StructureToPtr(mmi, lParam, true);
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo { public NativePoint Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public int Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
