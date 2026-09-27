using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace PhilipsControl.Models;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(property);
        OnChanged(property);
        return true;
    }
    protected virtual void OnChanged(string? property) { }
}

public sealed class TvRecord : ObservableObject
{
    private string _name = "Philips TV";
    private string _alias = "";
    private string _host = "";
    private string _model = "";
    private string _apiVersion = "6";
    private int _port = 1926;
    private bool _useHttps = true;
    private string _username = "";
    private string _password = "";
    private string _credentialBlob = "";
    private string _macAddress = "";
    private List<string> _favoriteApps = [];
    private bool _isOnline;
    private bool _isPoweredOn;
    private bool _powerStateKnown;
    private bool _authFailed;
    private int _volume;
    private int _maxVolume = 60;
    private bool _isMuted;
    private string _currentApp = "Unavailable";
    private string _currentActivity = "No activity detected";
    private string _currentInput = "Unavailable";
    private string _currentChannel = "Unavailable";
    private string _playbackStatus = "Unavailable";
    private string _lastSeen = "Never connected";
    private string _statusMessage = "No TV connected";
    private string _ambilightMode = "Unavailable";
    private string _ambilightStyle = "";
    private bool _ambilightOn;
    private bool _ambilightKnown;
    private bool _isActive;

    public string Name { get => _name; set => Set(ref _name, value); }
    /// <summary>User-chosen display name. Survives the TV reporting its own name on every refresh.</summary>
    public string Alias { get => _alias; set => Set(ref _alias, value); }
    public string Host { get => _host; set => Set(ref _host, value); }
    public string Model { get => _model; set => Set(ref _model, value); }
    public string ApiVersion { get => _apiVersion; set => Set(ref _apiVersion, value); }
    public int Port { get => _port; set => Set(ref _port, value); }
    public bool UseHttps { get => _useHttps; set => Set(ref _useHttps, value); }
    [JsonIgnore] public string Username { get => _username; set => Set(ref _username, value); }
    [JsonIgnore] public string Password { get => _password; set => Set(ref _password, value); }
    public string CredentialBlob { get => _credentialBlob; set => Set(ref _credentialBlob, value); }
    public string MacAddress { get => _macAddress; set => Set(ref _macAddress, value); }
    public List<string> FavoriteApps { get => _favoriteApps; set => Set(ref _favoriteApps, value ?? []); }
    public string LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }

    [JsonIgnore] public bool IsOnline { get => _isOnline; set => Set(ref _isOnline, value); }
    [JsonIgnore] public bool IsPoweredOn { get => _isPoweredOn; set => Set(ref _isPoweredOn, value); }
    [JsonIgnore] public bool PowerStateKnown { get => _powerStateKnown; set => Set(ref _powerStateKnown, value); }
    [JsonIgnore] public bool AuthFailed { get => _authFailed; set => Set(ref _authFailed, value); }
    [JsonIgnore] public int Volume { get => _volume; set => Set(ref _volume, value); }
    [JsonIgnore] public int MaxVolume { get => _maxVolume; set => Set(ref _maxVolume, value); }
    [JsonIgnore] public bool IsMuted { get => _isMuted; set => Set(ref _isMuted, value); }
    [JsonIgnore] public string CurrentApp { get => _currentApp; set => Set(ref _currentApp, value); }
    [JsonIgnore] public string CurrentActivity { get => _currentActivity; set => Set(ref _currentActivity, value); }
    [JsonIgnore] public string CurrentInput { get => _currentInput; set => Set(ref _currentInput, value); }
    [JsonIgnore] public string CurrentChannel { get => _currentChannel; set => Set(ref _currentChannel, value); }
    [JsonIgnore] public string PlaybackStatus { get => _playbackStatus; set => Set(ref _playbackStatus, value); }
    [JsonIgnore] public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    [JsonIgnore] public string AmbilightMode { get => _ambilightMode; set => Set(ref _ambilightMode, value); }
    [JsonIgnore] public string AmbilightStyle { get => _ambilightStyle; set => Set(ref _ambilightStyle, value); }
    [JsonIgnore] public bool AmbilightOn { get => _ambilightOn; set => Set(ref _ambilightOn, value); }
    [JsonIgnore] public bool AmbilightKnown { get => _ambilightKnown; set => Set(ref _ambilightKnown, value); }
    /// <summary>True for the TV currently controlled by the window.</summary>
    [JsonIgnore] public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : Alias.Trim();
    [JsonIgnore] public string ConnectionLabel => AuthFailed ? "Pairing required" : IsOnline ? "Online" : "Offline";
    [JsonIgnore] public string ModelLabel => string.IsNullOrWhiteSpace(Model) ? "Philips Smart TV" : Model;
    [JsonIgnore] public string PowerLabel => !PowerStateKnown ? "Unknown" : IsPoweredOn ? "On" : "Standby";
    [JsonIgnore] public bool IsActiveOn => IsOnline && (!PowerStateKnown || IsPoweredOn);
    [JsonIgnore] public string StateBadge => AuthFailed ? "PAIR" : !IsOnline ? "OFFLINE" : PowerStateKnown && !IsPoweredOn ? "STANDBY" : "ONLINE";
    [JsonIgnore] public string VolumeLabel => IsMuted ? "MUTED" : $"{Volume} / {MaxVolume}";
    [JsonIgnore] public double VolumePercent => MaxVolume <= 0 ? 0 : Math.Clamp(Volume * 100.0 / MaxVolume, 0, 100);
    [JsonIgnore] public string ApiLabel => $"JointSPACE {ApiVersion} · {(UseHttps ? "HTTPS" : "HTTP")}:{Port}";
    [JsonIgnore] public string AmbilightLabel => !AmbilightKnown ? "Unavailable" : AmbilightOn ? "On" : "Off";
    [JsonIgnore] public bool HasChannel => CurrentChannel is { Length: > 0 } c && c != "Unavailable";
    /// <summary>Source, channel and playback joined, leaving out whatever the TV does not report.</summary>
    [JsonIgnore] public string MediaSummary => string.Join("  ·  ", new[] { CurrentInput, CurrentChannel, PlaybackStatus }
        .Where(s => !string.IsNullOrWhiteSpace(s) && s != "Unavailable").Distinct()) is { Length: > 0 } summary ? summary : "Nothing reported by the TV";

    /// <summary>Re-evaluates state-colored bindings, e.g. after the accent color changed.</summary>
    public void RefreshStateBindings() => Raise(nameof(StateBadge));

    protected override void OnChanged(string? property)
    {
        switch (property)
        {
            case nameof(Name) or nameof(Alias): Raise(nameof(DisplayName)); break;
            case nameof(Model): Raise(nameof(ModelLabel)); break;
            case nameof(IsOnline) or nameof(AuthFailed) or nameof(IsPoweredOn) or nameof(PowerStateKnown):
                Raise(nameof(ConnectionLabel)); Raise(nameof(PowerLabel)); Raise(nameof(IsActiveOn)); Raise(nameof(StateBadge)); break;
            case nameof(Volume) or nameof(MaxVolume) or nameof(IsMuted):
                Raise(nameof(VolumeLabel)); Raise(nameof(VolumePercent)); break;
            case nameof(ApiVersion) or nameof(UseHttps) or nameof(Port): Raise(nameof(ApiLabel)); break;
            case nameof(AmbilightOn) or nameof(AmbilightKnown): Raise(nameof(AmbilightLabel)); break;
            case nameof(CurrentChannel): Raise(nameof(HasChannel)); Raise(nameof(MediaSummary)); break;
            case nameof(CurrentInput) or nameof(PlaybackStatus): Raise(nameof(MediaSummary)); break;
        }
    }
}

public sealed class TvApplication : ObservableObject
{
    private bool _isRunning;
    private bool _isFavorite;

    public string Label { get; set; } = "";
    public string Id { get; set; } = "";
    public int Order { get; set; }
    public string PackageName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Action { get; set; } = "android.intent.action.MAIN";
    public string Category { get; set; } = "android.intent.category.LAUNCHER";
    public string IntentJson { get; set; } = "";
    public bool IsRunning { get => _isRunning; set { if (Set(ref _isRunning, value)) Raise(nameof(StateLabel)); } }
    public bool IsFavorite { get => _isFavorite; set => Set(ref _isFavorite, value); }
    public string StateLabel => IsRunning ? "● RUNNING" : "";
    public string Initials => MakeInitials(Label);
    /// <summary>Stable key for favorites, independent of the list order the TV reports.</summary>
    public string FavoriteKey => PackageName.Length > 0 ? PackageName + "/" + ClassName : Label;

    public static string MakeInitials(string label)
    {
        var words = label.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || label == "Unavailable") return "–";
        if (words.Length == 1) return words[0].Length > 1 ? char.ToUpperInvariant(words[0][0]) + words[0][1..2].ToLowerInvariant() : words[0].ToUpperInvariant();
        return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));
    }
}

public sealed class TvChannel : ObservableObject
{
    private bool _isCurrent;
    public string Id { get; set; } = "";
    public string Ccid { get; set; } = "";
    public string Preset { get; set; } = "";
    public string Name { get; set; } = "";
    public string ListId { get; set; } = "";
    public string ListVersion { get; set; } = "";
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
    public string PresetLabel => int.TryParse(Preset, out var n) ? n.ToString("000") : Preset;
}

public sealed class TvSource
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class DiscoveryResult
{
    public string Host { get; set; } = "";
    public string Name { get; set; } = "Philips TV";
    public string Model { get; set; } = "";
    public string ApiVersion { get; set; } = "6";
    public int Port { get; set; } = 1926;
    public bool UseHttps { get; set; } = true;
    public bool RequiresPairing { get; set; }
    public string Details => string.Join("  ·  ", new[] { Host, Model, $"API {ApiVersion}", UseHttps ? "secure pairing" : "open API" }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public enum LogKind { Info, Success, Warning, Error, Command }

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Text { get; init; } = "";
    public LogKind Kind { get; init; }
    public string TimeLabel => Time.ToString("HH:mm:ss");
    public string KindLabel => Kind switch
    {
        LogKind.Success => "OK  ",
        LogKind.Warning => "WARN",
        LogKind.Error => "ERR ",
        LogKind.Command => "CMD ",
        _ => "INFO"
    };
}
