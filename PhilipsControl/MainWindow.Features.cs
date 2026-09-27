using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PhilipsControl.Models;
using PhilipsControl.Services;

namespace PhilipsControl;

public sealed class AmbilightStyleItem(AmbilightStyle style) : ObservableObject
{
    private bool _isSelected;
    public AmbilightStyle Style { get; } = style;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public partial class MainWindow
{
    private static readonly (string Key, string Label, Color Color)[] Accents =
    [
        ("green", "Terminal", Color.FromRgb(0x65, 0xE3, 0x94)),
        ("amber", "Amber", Color.FromRgb(0xFF, 0xB5, 0x47)),
        ("cyan", "Cyan", Color.FromRgb(0x4F, 0xD6, 0xFF)),
        ("violet", "Violet", Color.FromRgb(0xA9, 0x8B, 0xFF)),
        ("magenta", "Neon", Color.FromRgb(0xFF, 0x6A, 0xD5)),
        ("white", "Mono", Color.FromRgb(0xE4, 0xEC, 0xE6))
    ];
    private static readonly string[] Swatches = ["#FF2A1A", "#FF6A1A", "#FFB31A", "#FFE9C4", "#FFFFFF", "#7CFF4F", "#1AFFB0", "#1AC8FF", "#2A4BFF", "#8A2AFF", "#FF2AD4", "#FF4F8B"];

    private readonly ObservableCollection<TvChannel> _channels = [];
    private readonly List<AmbilightStyleItem> _styleItems = [.. PhilipsJointSpaceClient.AmbilightStyles.Select(s => new AmbilightStyleItem(s))];
    private readonly DispatcherTimer _volumeDebounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private readonly DispatcherTimer _colorDebounce = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private ListCollectionView _allAppsView = null!;
    private ListCollectionView _favoriteAppsView = null!;
    private ListCollectionView _pinnedView = null!;
    private ListCollectionView _channelsView = null!;
    private CancellationTokenSource? _scanCancellation;
    private bool _channelsLoaded;
    private bool _channelsLoading;
    private bool _suppressVolumeEvent;
    private DateTime _volumeHoldUntil;
    private int _pendingVolume = -1;
    private bool _suppressColorEvent;
    private Color _customColor = Color.FromRgb(0xFF, 0x6A, 0x1A);
    private DateTime? _sleepUntil;

    private void InitializeFeatures()
    {
        _allAppsView = new ListCollectionView(_apps) { Filter = AppMatchesSearch };
        _allAppsView.SortDescriptions.Add(new SortDescription(nameof(TvApplication.Label), ListSortDirection.Ascending));
        _favoriteAppsView = NewFavoritesView(AppMatchesSearch);
        _pinnedView = NewFavoritesView(_ => true);
        AllAppsList.ItemsSource = _allAppsView;
        FavoriteAppsList.ItemsSource = _favoriteAppsView;
        PinnedAppsList.ItemsSource = _pinnedView;
        _apps.CollectionChanged += (_, _) => UpdateAppsUi();

        _channelsView = new ListCollectionView(_channels) { Filter = ChannelMatchesSearch };
        ChannelsList.ItemsSource = _channelsView;

        VideoStyles.ItemsSource = _styleItems.Where(s => s.Style.Group == "video").ToList();
        AudioStyles.ItemsSource = _styleItems.Where(s => s.Style.Group == "audio").ToList();
        ColorStyles.ItemsSource = _styleItems.Where(s => s.Style.Group == "color").ToList();
        SwatchList.ItemsSource = Swatches;
        UpdateColorPreview();

        _volumeDebounce.Tick += async (_, _) => { _volumeDebounce.Stop(); await PushVolumeAsync(); };
        _colorDebounce.Tick += async (_, _) => { _colorDebounce.Stop(); await ApplyCustomColorAsync(false); };
        UpdateAppsUi();
    }

    private ListCollectionView NewFavoritesView(Predicate<object> extraFilter)
    {
        var view = new ListCollectionView(_apps) { Filter = o => o is TvApplication { IsFavorite: true } && extraFilter(o) };
        view.SortDescriptions.Add(new SortDescription(nameof(TvApplication.Label), ListSortDirection.Ascending));
        // Re-filter automatically when a star is toggled.
        view.IsLiveFiltering = true;
        view.LiveFilteringProperties.Add(nameof(TvApplication.IsFavorite));
        return view;
    }

    private void InitializePreferences()
    {
        CloseToTraySwitch.IsChecked = _settings.CloseToTray;
        ShortcutsSwitch.IsChecked = _settings.KeyboardShortcuts;
        NotificationsSwitch.IsChecked = _settings.Notifications;
        try { StartupSwitch.IsChecked = StartupRegistration.IsEnabled(); } catch { StartupSwitch.IsEnabled = false; }
        KeyboardStateText.Text = _settings.KeyboardShortcuts ? "ON" : "OFF";
        foreach (var chip in PollChips.Children.OfType<RadioButton>())
            chip.IsChecked = chip.CommandParameter as string == _settings.PollSeconds.ToString();
        foreach (var (key, label, color) in Accents)
        {
            var chip = new RadioButton
            {
                GroupName = "Accent",
                Style = (Style)FindResource("Chip"),
                CommandParameter = key,
                IsChecked = key == _settings.Accent,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = label, Margin = new Thickness(7, 0, 0, 0) }
                    }
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(chip, label);
            chip.Checked += (_, _) => { ApplyAccent(key); _settings.Save(); };
            AccentChips.Children.Add(chip);
        }
        VersionText.Text = $"Philips Control {VersionLabel}  ·  .NET {Environment.Version.ToString(2)}";
    }

    private void ApplyAccent(string key)
    {
        var accent = Array.Find(Accents, a => a.Key == key);
        if (accent.Key is null) accent = Accents[0];
        var color = accent.Color;
        _settings.Accent = accent.Key;
        var bg = Color.FromRgb(0x08, 0x0B, 0x08);
        var resources = Application.Current.Resources;
        resources["Accent"] = Frozen(color);
        resources["AccentSoft"] = Frozen(Blend(color, bg, 0.14));
        resources["AccentLine"] = Frozen(Blend(color, bg, 0.45));
        resources["AccentColor"] = color;
        _active?.RefreshStateBindings();
        if (IsInitialized && _active is not null) UpdateStatusUi();
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color color, Color background, double amount) => Color.FromRgb(
        (byte)(background.R + (color.R - background.R) * amount),
        (byte)(background.G + (color.G - background.G) * amount),
        (byte)(background.B + (color.B - background.B) * amount));

    // ───────────────────────────── Volume ─────────────────────────────

    /// <summary>True while the user is dragging the slider or a change is still on its way to the TV; polling must not snap it back.</summary>
    private bool VolumeHeld => VolumeSlider.IsMouseCaptureWithin || DateTime.UtcNow < _volumeHoldUntil;

    private void SyncVolumeSlider()
    {
        if (VolumeHeld) return;
        _suppressVolumeEvent = true;
        VolumeSlider.Maximum = Math.Max(1, _active.MaxVolume);
        VolumeSlider.Value = _active.Volume;
        _suppressVolumeEvent = false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolumeEvent || !IsInitialized) return;
        if (_client is null) { _suppressVolumeEvent = true; VolumeSlider.Value = e.OldValue; _suppressVolumeEvent = false; return; }
        var value = (int)Math.Round(e.NewValue);
        _volumeHoldUntil = DateTime.UtcNow.AddSeconds(2);
        _pendingVolume = value;
        _active.Volume = value;
        _volumeDebounce.Stop();
        _volumeDebounce.Start();
    }

    private async Task PushVolumeAsync()
    {
        if (_client is null || _pendingVolume < 0) return;
        var value = _pendingVolume;
        var client = _client;
        _pendingVolume = -1;
        try
        {
            await client.SetVolumeAsync(value);
            _active.IsMuted = false;
            Log($"VOLUME {value}", LogKind.Command);
        }
        catch (Exception ex)
        {
            Log("Volume change failed: " + ex.Message, LogKind.Error);
            Toast("Volume: " + ShortError(ex), LogKind.Error);
        }
        finally
        {
            _volumeHoldUntil = DateTime.UtcNow.AddSeconds(1.2);
        }
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_client is null) return;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + Math.Sign(e.Delta), 0, VolumeSlider.Maximum);
        e.Handled = true;
    }

    // ───────────────────────────── Apps ─────────────────────────────

    private bool AppMatchesSearch(object item) =>
        item is TvApplication app && (AppSearch.Text.Length == 0 || app.Label.Contains(AppSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase));

    private void AppSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _allAppsView.Refresh();
        _favoriteAppsView.Refresh();
        UpdateAppsUi();
    }

    private void UpdateAppsUi()
    {
        if (_allAppsView is null) return;
        AppsCountText.Text = AppSearch.Text.Length > 0 ? $"MATCHING APPS ({_allAppsView.Count} of {_apps.Count})" : $"ALL APPS ({_apps.Count})";
        FavoritesSection.Visibility = _favoriteAppsView.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoPinnedText.Visibility = _pinnedView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoAppsText.Visibility = _allAppsView.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoAppsText.Text = _apps.Count > 0 ? "No app matches the search." : _active.IsOnline ? "This TV did not provide an application list." : "Connect to a TV to load its application list.";
    }

    private async void LaunchApp_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) { ShowConnectNeeded(); return; }
        if ((sender as FrameworkElement)?.Tag is not TvApplication app) return;
        var client = _client;
        var record = _active;
        try
        {
            await client.LaunchApplicationAsync(app);
            Log("LAUNCH " + app.Label, LogKind.Command);
            Toast("Opening " + app.Label, LogKind.Success);
            await Task.Delay(900);
            if (ReferenceEquals(client, _client) && ReferenceEquals(record, _active)) await RefreshSnapshotAsync(false);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(client, _client) || !ReferenceEquals(record, _active)) return;
            Log($"Could not launch {app.Label}: {ex.Message}", LogKind.Error);
            Toast($"Could not launch {app.Label} on this TV", LogKind.Error);
        }
    }

    private void FavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TvApplication app) return;
        var favorites = _active.FavoriteApps;
        favorites.Remove(app.FavoriteKey);
        if (app.IsFavorite) favorites.Add(app.FavoriteKey);
        SaveDevices();
        UpdateAppsUi();
    }

    private async void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) { ShowConnectNeeded(); return; }
        await RefreshSnapshotAsync(includeApplications: true);
        Toast($"{_apps.Count} apps loaded", LogKind.Info);
    }

    // ───────────────────────────── Channels & inputs ─────────────────────────────

    private void ResetMediaState()
    {
        _channels.Clear();
        _channelsLoaded = false;
        ChannelsStatus.Text = "Load the channel list from the TV to zap with one click.";
        SourcesList.ItemsSource = null;
        SourcesCard.Visibility = Visibility.Collapsed;
    }

    private void EnsureChannelsLoaded()
    {
        if (!_channelsLoaded && !_channelsLoading && _client is not null && _active.IsActiveOn) _ = LoadChannelsAsync(false);
    }

    private async void LoadChannels_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) { ShowConnectNeeded(); return; }
        await LoadChannelsAsync(true);
    }

    private async Task LoadChannelsAsync(bool announce)
    {
        if (_client is null || _channelsLoading) return;
        var client = _client;
        _channelsLoading = true;
        LoadChannelsButton.IsEnabled = false;
        ChannelsStatus.Text = "Loading channels from the TV...";
        try
        {
            var channels = await client.GetChannelsAsync();
            if (!ReferenceEquals(client, _client)) return;
            _channels.Clear();
            foreach (var channel in channels) _channels.Add(channel);
            _channelsLoaded = true;
            MarkCurrentChannel();
            ChannelsStatus.Text = channels.Count == 0 ? "The TV reported an empty channel list." : $"{channels.Count} channels. Click one to switch.";
            Log($"Loaded {channels.Count} channels.", LogKind.Info);
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(client, _client)) return;
            _channelsLoaded = true;
            ChannelsStatus.Text = "This TV did not provide a channel list (" + ShortError(ex) + ").";
            if (announce) Toast("Channel list unavailable on this TV", LogKind.Warning);
        }
        finally
        {
            _channelsLoading = false;
            LoadChannelsButton.IsEnabled = true;
        }
    }

    private bool ChannelMatchesSearch(object item)
    {
        if (item is not TvChannel channel) return false;
        var query = ChannelSearch.Text.Trim();
        return query.Length == 0 || channel.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || channel.Preset.StartsWith(query, StringComparison.Ordinal);
    }

    private void ChannelSearch_TextChanged(object sender, TextChangedEventArgs e) => _channelsView?.Refresh();

    private void MarkCurrentChannel()
    {
        var current = _active.CurrentChannel;
        foreach (var channel in _channels) channel.IsCurrent = current == $"{channel.Preset} · {channel.Name}";
    }

    private async void Channel_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || (sender as FrameworkElement)?.Tag is not TvChannel channel) return;
        try
        {
            await _client.SwitchChannelAsync(channel);
            Log($"CHANNEL {channel.Preset} {channel.Name}", LogKind.Command);
            Toast($"Switching to {channel.Name}", LogKind.Success);
            foreach (var c in _channels) c.IsCurrent = ReferenceEquals(c, channel);
            QueueRefresh();
        }
        catch (Exception ex)
        {
            Log($"Channel switch failed: {ex.Message}", LogKind.Error);
            Toast("Could not switch channel: " + ShortError(ex), LogKind.Error);
        }
    }

    private async Task LoadSourcesAsync(int connectionId)
    {
        if (_client is null || _client.IsApi6) return;
        try
        {
            var sources = await _client.GetSourcesAsync();
            if (connectionId != _connectionId) return;
            SourcesList.ItemsSource = sources;
            SourcesCard.Visibility = sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    private async void Source_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || (sender as FrameworkElement)?.Tag is not TvSource source) return;
        try
        {
            await _client.SelectSourceAsync(source);
            Log("SOURCE " + source.Name, LogKind.Command);
            QueueRefresh();
        }
        catch (Exception ex) { Toast("Could not select input: " + ShortError(ex), LogKind.Error); }
    }

    // ───────────────────────────── Ambilight ─────────────────────────────

    private void UpdateAmbilightSelection()
    {
        foreach (var item in _styleItems)
            item.IsSelected = item.Style.MenuSetting.Equals(_active.AmbilightStyle, StringComparison.OrdinalIgnoreCase);
    }

    private async void AmbilightSwitch_Click(object sender, RoutedEventArgs e)
    {
        var desired = AmbilightSwitch.IsChecked == true;
        if (_client is null) { _active.AmbilightOn = !desired; ShowConnectNeeded(); return; }
        await SetAmbilightPowerAsync(desired);
    }

    private async void AmbilightQuickToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) { ShowConnectNeeded(); return; }
        if (!_active.AmbilightKnown) { await RunRemoteKeyAsync("AmbilightOnOff"); return; }
        await SetAmbilightPowerAsync(!_active.AmbilightOn);
    }

    private async Task SetAmbilightPowerAsync(bool on)
    {
        var client = _client!;
        var record = _active;
        var previous = record.AmbilightOn;
        record.AmbilightOn = on;
        try
        {
            await client.SetAmbilightAsync(on);
            record.AmbilightKnown = true;
            Log("AMBILIGHT " + (on ? "ON" : "OFF"), LogKind.Command);
            QueueRefresh();
        }
        catch (Exception ex)
        {
            record.AmbilightOn = previous;
            Log("Ambilight power failed: " + ex.Message, LogKind.Error);
            Toast("Ambilight control is not available on this TV", LogKind.Error);
        }
    }

    private async void AmbilightStyle_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) { ShowConnectNeeded(); return; }
        if ((sender as FrameworkElement)?.Tag is not AmbilightStyleItem item) return;
        try
        {
            await _client.SetAmbilightStyleAsync(item.Style);
            _active.AmbilightStyle = item.Style.MenuSetting;
            _active.AmbilightMode = PhilipsJointSpaceClient.FriendlyAmbilightName(item.Style.StyleName, item.Style.MenuSetting);
            _active.AmbilightOn = true;
            Log($"AMBILIGHT {item.Style.StyleName} / {item.Style.MenuSetting}", LogKind.Command);
            QueueRefresh();
        }
        catch (Exception ex)
        {
            Log($"Ambilight style {item.Style.Label} failed: {ex.Message}", LogKind.Error);
            Toast($"'{item.Style.Label}' is not supported by this TV", LogKind.Warning);
        }
    }

    private void ColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || HueSlider is null || SatSlider is null || BrightSlider is null) return;
        _customColor = FromHsv(HueSlider.Value, SatSlider.Value / 100, BrightSlider.Value / 100);
        UpdateColorPreview();
        if (!_suppressColorEvent && LiveColorSwitch?.IsChecked == true && _client is not null)
        {
            _colorDebounce.Stop();
            _colorDebounce.Start();
        }
    }

    private void UpdateColorPreview()
    {
        if (PreviewGlow is null) return;
        PreviewGlow.Color = _customColor;
        ColorHexText.Text = $"#{_customColor.R:X2}{_customColor.G:X2}{_customColor.B:X2}";
        SatGradientEnd.Color = FromHsv(HueSlider.Value, 1, 1);
        BrightGradientEnd.Color = FromHsv(HueSlider.Value, SatSlider.Value / 100, 1);
    }

    private async void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string hex) return;
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var (h, s, v) = ToHsv(color);
        _suppressColorEvent = true;
        HueSlider.Value = h;
        SatSlider.Value = s * 100;
        BrightSlider.Value = Math.Max(BrightSlider.Minimum, v * 100);
        _suppressColorEvent = false;
        _customColor = color;
        UpdateColorPreview();
        if (_client is not null) await ApplyCustomColorAsync(true);
    }

    private async void ApplyColor_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) { ShowConnectNeeded(); return; }
        await ApplyCustomColorAsync(true);
    }

    private async Task ApplyCustomColorAsync(bool announce)
    {
        if (_client is null) return;
        var color = _customColor;
        try
        {
            await _client.SetAmbilightColorAsync(color.R, color.G, color.B);
            _active.AmbilightStyle = "CUSTOM";
            _active.AmbilightMode = "Custom color";
            Log($"AMBILIGHT COLOR #{color.R:X2}{color.G:X2}{color.B:X2}", LogKind.Command);
            if (announce) Toast("Ambilight color applied", LogKind.Success);
        }
        catch (Exception ex)
        {
            Log("Ambilight color failed: " + ex.Message, LogKind.Error);
            if (announce) Toast("This TV does not accept custom Ambilight colors", LogKind.Warning);
        }
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = (hue % 360 + 360) % 360;
        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return (hue < 0 ? hue + 360 : hue, max == 0 ? 0 : delta / max, max);
    }

    // ───────────────────────────── Sleep timer ─────────────────────────────

    private void SleepChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { CommandParameter: string value } && int.TryParse(value, out var minutes)) SetSleepTimer(minutes);
    }

    private void SleepBadge_Click(object sender, MouseButtonEventArgs e) => SetSleepTimer(0);

    private void SetSleepTimer(int minutes)
    {
        if (minutes <= 0)
        {
            if (_sleepUntil is not null) Log("Sleep timer cancelled.", LogKind.Info);
            _sleepUntil = null;
        }
        else
        {
            if (_active.Host.Length == 0) { ShowConnectNeeded(); minutes = 0; }
            else
            {
                _sleepUntil = DateTime.Now.AddMinutes(minutes);
                Log($"Sleep timer set: standby at {_sleepUntil:HH:mm}.", LogKind.Info);
                Toast($"{_active.DisplayName} turns off at {_sleepUntil:HH:mm}", LogKind.Success);
            }
        }
        foreach (var chip in SleepChips.Children.OfType<RadioButton>())
            chip.IsChecked = chip.CommandParameter as string == (_sleepUntil is null ? "0" : minutes.ToString());
        UpdateSleepUi();
    }

    private void SleepTick()
    {
        if (_sleepUntil is null) return;
        if (DateTime.Now >= _sleepUntil)
        {
            _sleepUntil = null;
            foreach (var chip in SleepChips.Children.OfType<RadioButton>()) chip.IsChecked = chip.CommandParameter as string == "0";
            _ = SleepExpiredAsync();
        }
        UpdateSleepUi();
    }

    private async Task SleepExpiredAsync()
    {
        Log("Sleep timer finished. Switching the TV to standby.", LogKind.Info);
        if (!IsVisible) _tray?.Balloon("Sleep timer", _active.DisplayName + " is switching to standby.");
        try
        {
            if (_client is not null) await _client.SetPowerAsync(false);
            _active.IsPoweredOn = false;
            _active.PowerStateKnown = true;
            Toast("Sleep timer: TV switched to standby", LogKind.Success);
            QueueRefresh();
        }
        catch (Exception ex) { Log("Sleep timer could not switch the TV off: " + ex.Message, LogKind.Error); }
    }

    private void UpdateSleepUi()
    {
        if (_sleepUntil is { } until)
        {
            var left = until - DateTime.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            var text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
            SleepBadge.Visibility = Visibility.Visible;
            SleepBadgeText.Text = text;
            StatusSleep.Text = "SLEEP " + text;
            SleepStatusText.Text = $"Standby at {until:HH:mm}  ·  {text} left";
        }
        else
        {
            SleepBadge.Visibility = Visibility.Collapsed;
            StatusSleep.Text = "";
            SleepStatusText.Text = "The TV switches to standby when the timer ends.";
        }
    }

    // ───────────────────────────── Devices ─────────────────────────────

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (_discovering) { _scanCancellation?.Cancel(); return; }
        await ScanForTvsAsync();
    }

    private async Task ScanForTvsAsync()
    {
        if (_discovering) return;
        _discovering = true;
        _scanCancellation = new CancellationTokenSource();
        DiscoverButtonText.Text = "STOP SCAN";
        ScanProgress.Visibility = Visibility.Visible;
        ScanProgress.Value = 0;
        _foundTvs.Clear();
        Log("Network scan started.", LogKind.Info);
        var progress = new Progress<(int Done, int Total, string Message)>(p =>
        {
            DiscoveryStatus.Text = p.Message;
            ScanProgress.Maximum = p.Total;
            ScanProgress.Value = p.Done;
        });
        void OnFound(DiscoveryResult device) => Dispatcher.InvokeAsync(() =>
        {
            if (_foundTvs.Any(existing => existing.Host.Equals(device.Host, StringComparison.OrdinalIgnoreCase))) return;
            _foundTvs.Add(device);
            Log($"Found {device.Name} at {device.Host}.", LogKind.Success);
        });
        try
        {
            var found = await _discovery.DiscoverAsync(_scanCancellation.Token, progress, OnFound);
            DiscoveryStatus.Text = found.Count == 0
                ? "No Philips TVs found. Check that the PC and TV share a network and that the TV is on, or enter its IP address."
                : $"Found {found.Count} Philips TV{(found.Count == 1 ? "" : "s")}. Choose one to add it.";
            Log($"Network scan finished: {found.Count} TV(s).", LogKind.Info);
        }
        catch (OperationCanceledException) { DiscoveryStatus.Text = "Scan stopped."; }
        catch (Exception ex) { DiscoveryStatus.Text = "Network scan failed: " + ex.Message; Log(DiscoveryStatus.Text, LogKind.Error); }
        finally
        {
            _discovering = false;
            _scanCancellation.Dispose();
            _scanCancellation = null;
            DiscoverButtonText.Text = "SCAN NETWORK";
            ScanProgress.Visibility = Visibility.Hidden;
            UpdateStatusUi();
        }
    }

    private void HostInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; AddHost_Click(sender, e); }
    }

    private async void AddHost_Click(object sender, RoutedEventArgs e)
    {
        var host = HostInput.Text.Trim();
        if (host.Length == 0) { DiscoveryStatus.Text = "Enter the TV's local IP address or hostname first."; HostInput.Focus(); return; }
        DiscoveryStatus.Text = $"Checking {host} for Philips JointSPACE...";
        try
        {
            var device = await PhilipsJointSpaceClient.ProbeHostAsync(host);
            if (device is null)
            {
                DiscoveryStatus.Text = "No Philips JointSPACE TV answered at this address. Check the IP, that the TV is on, and that remote control apps are allowed on the TV.";
                return;
            }
            if (!_foundTvs.Any(tv => tv.Host.Equals(device.Host, StringComparison.OrdinalIgnoreCase))) _foundTvs.Add(device);
            HostInput.Text = "";
            await ConnectDiscoveredAsync(device);
        }
        catch (Exception ex) { DiscoveryStatus.Text = ex.Message; }
    }

    private async void FoundTV_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DiscoveryResult device) await ConnectDiscoveredAsync(device);
    }

    private async Task ConnectDiscoveredAsync(DiscoveryResult device)
    {
        var record = _savedTvs.FirstOrDefault(tv => tv.Host.Equals(device.Host, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            record = new TvRecord { Name = device.Name, Host = device.Host, Model = device.Model, ApiVersion = device.ApiVersion, Port = device.Port, UseHttps = device.UseHttps };
            _savedTvs.Add(record);
            Log($"Added {device.Name} ({device.Host}).", LogKind.Info);
        }
        else
        {
            if (device.Model.Length > 0) record.Model = device.Model;
            // Switching protocol (e.g. after a firmware update) invalidates the old pairing.
            if (record.UseHttps != device.UseHttps || record.Port != device.Port) { record.Username = ""; record.Password = ""; }
            record.ApiVersion = device.ApiVersion;
            record.Port = device.Port;
            record.UseHttps = device.UseHttps;
        }
        SaveDevices();
        await ConnectRecordAsync(record, true);
    }

    private async void SavedTV_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TvRecord tv) await ConnectRecordAsync(tv, true);
    }

    private void RenameTV_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TvRecord tv) return;
        var name = TerminalDialog.AskText(this, "Rename TV", $"Choose a display name for {tv.Host}. Leave empty to use the name reported by the TV ({tv.Name}).", tv.Alias, tv.Name);
        if (name is null) return;
        tv.Alias = name;
        SaveDevices();
        UpdateStatusUi();
    }

    private void RemoveTV_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TvRecord tv) return;
        if (!TerminalDialog.Confirm(this, "Remove TV", $"Remove {tv.DisplayName} ({tv.Host}) and its pairing from this PC? You will need the TV's PIN to add it again.", "REMOVE", danger: true)) return;
        _savedTvs.Remove(tv);
        SaveDevices();
        Log($"Removed {tv.DisplayName}.", LogKind.Info);
        if (!ReferenceEquals(tv, _active)) return;
        _connectionId++;
        _refreshId++;
        _pollTimer.Stop();
        _connectionCancellation?.Cancel();
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _client?.Dispose();
        _client = null;
        _needsReconnect = false;
        _apps.Clear();
        ResetMediaState();
        SetSleepTimer(0);
        SetActive(new TvRecord { Name = "No TV connected", StatusMessage = "Add a TV to get started.", LastSeen = "—" });
        _settings.ActiveHost = "";
        if (_savedTvs.Count > 0) _ = ConnectRecordAsync(_savedTvs[0], false);
    }

    private async void DetectMac_Click(object sender, RoutedEventArgs e)
    {
        if (_active.Host.Length == 0) { ShowConnectNeeded(); return; }
        var mac = await WakeOnLan.ResolveMacAsync(_active.Host);
        if (mac.Length == 0) { Toast("MAC not found. The TV must be on and on the same subnet.", LogKind.Warning); return; }
        MacInput.Text = mac;
        _active.MacAddress = mac;
        SaveDevices();
        Toast("MAC address detected and saved", LogKind.Success);
    }

    private void SaveMac_Click(object sender, RoutedEventArgs e)
    {
        var text = MacInput.Text.Trim();
        var mac = text.Length == 0 ? "" : WakeOnLan.Normalize(text);
        if (mac is null)
        {
            TerminalDialog.Inform(this, "Check MAC address", "Enter a MAC address such as AA:BB:CC:DD:EE:FF (dashes or no separators work too).");
            return;
        }
        _active.MacAddress = mac;
        MacInput.Text = mac;
        if (_savedTvs.Contains(_active)) SaveDevices();
        UpdateStatusUi();
        Toast(mac.Length == 0 ? "MAC address cleared" : "Wake-on-LAN address saved", LogKind.Success);
    }

    private async void SendWake_Click(object sender, RoutedEventArgs e)
    {
        var mac = WakeOnLan.Normalize(MacInput.Text.Trim());
        if (mac is null) { Toast("Enter a valid MAC address first", LogKind.Warning); return; }
        try
        {
            await WakeOnLan.SendAsync(mac, _active.Host);
            Log($"Wake-on-LAN sent to {mac}.", LogKind.Command);
            Toast("Wake-on-LAN packet sent", LogKind.Success);
        }
        catch (Exception ex) { Toast("Wake-on-LAN failed: " + ex.Message, LogKind.Error); }
    }

    private async void Repair_Click(object sender, RoutedEventArgs e) => await RepairAsync();

    private async Task RepairAsync()
    {
        if (_active.Host.Length == 0) { ShowConnectNeeded(); return; }
        if (!_active.UseHttps)
        {
            Toast("This TV uses an open API and does not need pairing", LogKind.Info);
            await ConnectRecordAsync(_active, true);
            return;
        }
        if (!TerminalDialog.Confirm(this, "Pair again", $"Forget the stored pairing for {_active.DisplayName} and request a new PIN? Make sure the TV is switched on.", "PAIR")) return;
        _active.Username = "";
        _active.Password = "";
        SaveDevices();
        Log("Stored pairing cleared. Requesting a new PIN.", LogKind.Info);
        await ConnectRecordAsync(_active, true);
    }

    private void SaveDevices()
    {
        try { _store.Save(_savedTvs); }
        catch (Exception ex)
        {
            Log("Could not save TV settings: " + ex.Message, LogKind.Error);
            DiscoveryStatus.Text = "Could not save TV settings: " + ex.Message;
        }
    }

    // ───────────────────────────── Preferences ─────────────────────────────

    private void PreferenceSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = CloseToTraySwitch.IsChecked == true;
        _settings.KeyboardShortcuts = ShortcutsSwitch.IsChecked == true;
        _settings.Notifications = NotificationsSwitch.IsChecked == true;
        KeyboardStateText.Text = _settings.KeyboardShortcuts ? "ON" : "OFF";
        _settings.Save();
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        var enable = StartupSwitch.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enable);
            Toast(enable ? "Philips Control will start with Windows" : "Autostart disabled", LogKind.Success);
        }
        catch (Exception ex)
        {
            StartupSwitch.IsChecked = !enable;
            Toast("Could not change autostart: " + ex.Message, LogKind.Error);
        }
    }

    private void PollChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { CommandParameter: string value } || !int.TryParse(value, out var seconds)) return;
        _settings.PollSeconds = seconds;
        _pollTimer.Interval = TimeSpan.FromSeconds(seconds);
        _settings.Save();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(TvStore.DataFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TvStore.DataFolder}\"") { UseShellExecute = true });
    }
}
