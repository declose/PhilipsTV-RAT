using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using PhilipsControl.Models;

namespace PhilipsControl.Services;

public sealed record AmbilightStyle(string Group, string StyleName, string MenuSetting, string Label);

public sealed class PhilipsJointSpaceClient : IDisposable
{
    private static readonly byte[] PairingSecret = Convert.FromBase64String("ZmVay1EQVFOaZhwQ4Kv81ypLAZNczV9sG4KkseXWn1NEk6cXmPKO/MCa9sryslvLCFMnNe4Z4CPXzToowvhHvA==");

    // One shared client for unauthenticated probing: discovery would otherwise create hundreds of sockets.
    private static readonly HttpClient ProbeHttp = new(new SocketsHttpHandler
    {
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    }) { Timeout = TimeSpan.FromSeconds(4) };

    public static readonly IReadOnlyList<AmbilightStyle> AmbilightStyles =
    [
        new("video", "FOLLOW_VIDEO", "STANDARD", "Standard"),
        new("video", "FOLLOW_VIDEO", "NATURAL", "Natural"),
        new("video", "FOLLOW_VIDEO", "IMMERSIVE", "Vivid"),
        new("video", "FOLLOW_VIDEO", "GAME", "Game"),
        new("video", "FOLLOW_VIDEO", "COMFORT", "Comfort"),
        new("video", "FOLLOW_VIDEO", "RELAX", "Relax"),
        new("audio", "FOLLOW_AUDIO", "ENERGY_ADAPTIVE_BRIGHTNESS", "Lumina"),
        new("audio", "FOLLOW_AUDIO", "ENERGY_ADAPTIVE_COLORS", "Colora"),
        new("audio", "FOLLOW_AUDIO", "VU_METER", "Retro"),
        new("audio", "FOLLOW_AUDIO", "SPECTRUM_ANALYZER", "Spectrum"),
        new("audio", "FOLLOW_AUDIO", "KNIGHT_RIDER_CLOCKWISE", "Scanner"),
        new("audio", "FOLLOW_AUDIO", "KNIGHT_RIDER_ALTERNATING", "Rhythm"),
        new("audio", "FOLLOW_AUDIO", "RANDOM_PIXEL_FLASH", "Flash"),
        new("audio", "FOLLOW_AUDIO", "STROBO", "Strobe"),
        new("audio", "FOLLOW_AUDIO", "PARTY", "Party"),
        new("color", "FOLLOW_COLOR", "HOT_LAVA", "Hot lava"),
        new("color", "FOLLOW_COLOR", "DEEP_WATER", "Deep water"),
        new("color", "FOLLOW_COLOR", "FRESH_NATURE", "Fresh nature"),
        new("color", "FOLLOW_COLOR", "ISF", "Warm white"),
        new("color", "FOLLOW_COLOR", "PTA_LOUNGE", "Cool white"),
    ];

    private readonly TvRecord _tv;
    private readonly HttpClient _http;
    private readonly string _root;
    private readonly string _api;
    private string _activePackage = "";
    private string _activeClassName = "";
    private string _activeReportedLabel = "";
    private int _unauthorizedCount;
    private Dictionary<string, TvChannel>? _channelIndex;

    public PhilipsJointSpaceClient(TvRecord tv)
    {
        _tv = tv;
        _root = $"{(tv.UseHttps ? "https" : "http")}://{tv.Host}:{tv.Port}";
        _api = $"/{tv.ApiVersion}/";
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            // Philips TVs choke when flooded with parallel connections.
            MaxConnectionsPerServer = 4,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        if (!string.IsNullOrWhiteSpace(tv.Username))
            handler.Credentials = new NetworkCredential(tv.Username, tv.Password);
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    public bool IsApi6 => _tv.ApiVersion.StartsWith('6');

    public async Task<bool> CheckOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await ProbeHttp.GetAsync($"{_root}{_api}system", ct);
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    public static async Task<DiscoveryResult?> ProbeHostAsync(string host, CancellationToken ct = default)
    {
        var address = await ResolveIPv4Async(host, ct);
        if (address is null) return null;
        host = address.ToString();
        var routes = new[] { (Port: 1926, Https: true, Version: "6"), (Port: 1925, Https: false, Version: "6"), (Port: 1925, Https: false, Version: "1") };
        foreach (var route in routes)
        {
            try
            {
                using var response = await ProbeHttp.GetAsync($"{(route.Https ? "https" : "http")}://{host}:{route.Port}/{route.Version}/system", ct);
                if (!response.IsSuccessStatusCode && !(route.Https && response.StatusCode == HttpStatusCode.Unauthorized)) continue;
                string name = "Philips TV", model = "";
                if (response.IsSuccessStatusCode)
                {
                    var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                    name = ReadString(json, "name") ?? name;
                    model = ReadString(json, "model") ?? "";
                }
                return new() { Host = host, Name = name, Model = model, ApiVersion = route.Version, Port = route.Port, UseHttps = route.Https, RequiresPairing = response.StatusCode == HttpStatusCode.Unauthorized };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return null;
    }

    private static async Task<IPAddress?> ResolveIPv4Async(string host, CancellationToken ct)
    {
        host = host.Trim();
        if (IPAddress.TryParse(host, out var parsed)) return parsed.AddressFamily == AddressFamily.InterNetwork ? parsed : null;
        if (Uri.CheckHostName(host) != UriHostNameType.Dns) return null;
        try { return (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, ct)).FirstOrDefault(); }
        catch (SocketException) { return null; }
    }

    public async Task<(string Username, string Password)> PairAsync(Func<Task<string?>> pinPrompt, CancellationToken ct = default)
    {
        var deviceId = RandomString(16);
        var device = new JsonObject
        {
            ["device_name"] = Environment.MachineName,
            ["device_os"] = "Windows",
            ["app_name"] = "Philips Control",
            ["type"] = "native",
            ["app_id"] = "app.id",
            ["id"] = deviceId
        };
        var requestBody = new JsonObject
        {
            ["scope"] = new JsonArray("read", "write", "control"),
            ["device"] = device.DeepClone()
        };
        using var first = await ProbeHttp.PostAsync($"{_root}/6/pair/request", JsonContent(requestBody), ct);
        first.EnsureSuccessStatusCode();
        var response = JsonNode.Parse(await first.Content.ReadAsStringAsync(ct));
        var error = ReadString(response, "error_id");
        if (error is not null && !error.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(error.Equals("CONCURRENT_PAIRING", StringComparison.OrdinalIgnoreCase)
                ? "The TV is already showing a pairing PIN for another device. Close it on the TV and try again."
                : $"The TV refused pairing ({error}).");
        var timestampNode = response?["timestamp"]?.DeepClone() ?? throw new InvalidOperationException("The TV did not return a pairing timestamp.");
        var timestamp = timestampNode.ToString();
        var authKey = ReadString(response, "auth_key") ?? throw new InvalidOperationException("The TV did not return a pairing key.");
        var pin = await pinPrompt();
        if (string.IsNullOrWhiteSpace(pin)) throw new OperationCanceledException("Pairing cancelled.");
        pin = pin.Trim();
        var toSign = Encoding.UTF8.GetBytes(timestamp + pin);
        var hmac = HMACSHA1.HashData(PairingSecret, toSign);
        var signature = Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexStringLower(hmac)));

        var grantBody = new JsonObject
        {
            ["auth"] = new JsonObject
            {
                ["auth_AppId"] = "1",
                ["pin"] = pin,
                ["auth_timestamp"] = timestampNode.DeepClone(),
                ["auth_signature"] = signature
            },
            ["device"] = device.DeepClone()
        };
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(deviceId, authKey),
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = false
        };
        using var pairingClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        using var grant = await pairingClient.PostAsync($"{_root}/6/pair/grant", JsonContent(grantBody), ct);
        var grantText = await grant.Content.ReadAsStringAsync(ct);
        if (!grant.IsSuccessStatusCode)
            throw new InvalidOperationException(grant.StatusCode == HttpStatusCode.Unauthorized
                ? "The PIN was rejected. Check the PIN shown on the TV and try pairing again."
                : $"Pairing failed (HTTP {(int)grant.StatusCode}). {grantText}");
        return (deviceId, authKey);
    }

    public async Task<JsonNode?> GetJsonAsync(string endpoint, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{_root}{_api}{endpoint.TrimStart('/')}", ct);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    public async Task PostJsonAsync(string endpoint, JsonNode payload, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"{_root}{_api}{endpoint.TrimStart('/')}", JsonContent(payload), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) _tv.AuthFailed = true;
        response.EnsureSuccessStatusCode();
    }

    public async Task RefreshSnapshotAsync(ICollection<TvApplication> apps, CancellationToken ct = default, bool includeApplications = true)
    {
        _unauthorizedCount = 0;
        var system = TryGet("system", ct);
        var audio = TryGet("audio/volume", ct);
        var power = TryGet("powerstate", ct);
        var activity = IsApi6 ? TryGet("activities/current", ct) : Task.FromResult<JsonNode?>(null);
        Task<JsonNode?> appList = includeApplications && IsApi6 ? TryGet("applications", ct) : Task.FromResult<JsonNode?>(null);
        var ambilight = TryGet("ambilight/power", ct);
        var ambilightConfig = IsApi6 ? TryGet("ambilight/currentconfiguration", ct) : TryGet("ambilight/mode", ct);
        var source = IsApi6 ? Task.FromResult<JsonNode?>(null) : TryGet("sources/current", ct);
        var channel = IsApi6 ? Task.FromResult<JsonNode?>(null) : TryGet("channels/current", ct);
        await Task.WhenAll(system, audio, power, activity, appList, ambilight, ambilightConfig, source, channel);

        var systemJson = await system;
        if (systemJson is not null)
        {
            _tv.Name = ReadString(systemJson, "name") ?? _tv.Name;
            _tv.Model = ReadString(systemJson, "model") ?? _tv.Model;
        }
        var volumeJson = await audio;
        if (volumeJson is not null)
        {
            // Raise MaxVolume first so the slider range is never narrower than the reported level.
            _tv.MaxVolume = Math.Max(1, ReadInt(volumeJson, "max") ?? _tv.MaxVolume);
            _tv.Volume = ReadInt(volumeJson, "current") ?? _tv.Volume;
            _tv.IsMuted = ReadBool(volumeJson, "muted") ?? _tv.IsMuted;
        }
        var powerJson = await power;
        if (powerJson is not null)
        {
            var powerState = ReadString(powerJson, "powerstate") ?? ReadString(powerJson, "state") ?? "";
            if (powerState.Length > 0)
            {
                _tv.PowerStateKnown = true;
                _tv.IsPoweredOn = powerState.Equals("on", StringComparison.OrdinalIgnoreCase);
            }
        }
        var activityJson = await activity;
        if (activityJson is not null)
        {
            var intent = GetProperty(activityJson, "intent");
            var nestedActivity = GetProperty(activityJson, "activity");
            var component = GetProperty(activityJson, "component") ?? GetProperty(intent, "component") ?? GetProperty(nestedActivity, "component");
            var rawIntent = intent is JsonValue ? intent.ToString() : "";
            _activePackage = ReadString(component, "packageName") ?? ReadString(activityJson, "packageName") ??
                FindStringProperty(activityJson, "packageName") ?? ParseIntentValue(rawIntent, "pkg") ?? "";
            _activeClassName = ReadString(component, "className") ?? ReadString(activityJson, "className") ??
                FindStringProperty(activityJson, "className") ?? ParseIntentComponent(rawIntent, _activePackage) ?? "";
            _activeReportedLabel = ReadString(activityJson, "label") ?? ReadString(activityJson, "name") ?? "";
        }
        else
        {
            _activePackage = "";
            _activeClassName = "";
            _activeReportedLabel = "";
        }
        if (_tv.PowerStateKnown && !_tv.IsPoweredOn)
        {
            _activePackage = "";
            _activeClassName = "";
            _activeReportedLabel = "";
        }
        var appJson = await appList;
        if (includeApplications && FindApplicationArray(appJson) is { } appArray)
        {
            apps.Clear();
            var index = 0;
            foreach (var node in appArray)
            {
                index++;
                var app = ParseApplication(node, index);
                app.IsFavorite = _tv.FavoriteApps.Contains(app.FavoriteKey);
                apps.Add(app);
            }
        }
        var activeApp = apps.FirstOrDefault(app => !string.IsNullOrWhiteSpace(_activeClassName) &&
            app.ClassName.Equals(_activeClassName, StringComparison.OrdinalIgnoreCase));
        activeApp ??= apps.FirstOrDefault(app => !string.IsNullOrWhiteSpace(_activePackage) &&
            app.PackageName.Equals(_activePackage, StringComparison.OrdinalIgnoreCase));
        var activeNameHint = !string.IsNullOrWhiteSpace(_activeReportedLabel) &&
            !LooksLikeTechnicalName(_activeReportedLabel, _activePackage, _activeClassName)
                ? _activeReportedLabel.Trim()
                : AppName(_activePackage, _activeClassName);
        activeApp ??= apps.FirstOrDefault(app => !string.IsNullOrWhiteSpace(activeNameHint) &&
            app.Label.Equals(activeNameHint, StringComparison.OrdinalIgnoreCase));
        foreach (var app in apps) app.IsRunning = ReferenceEquals(app, activeApp);

        var sourceJson = await source;
        var channelJson = await channel;
        if (!string.IsNullOrWhiteSpace(_activePackage) || !string.IsNullOrWhiteSpace(_activeClassName) || !string.IsNullOrWhiteSpace(_activeReportedLabel))
        {
            _tv.CurrentApp = GetDisplayAppName(activeApp, _activePackage, _activeClassName, _activeReportedLabel);
            _tv.CurrentActivity = _tv.CurrentApp == "Unavailable" ? "No app detected" : "Currently open on TV";
            // Judge by package, not label: the tuner app's own label is often "Watch TV" or a localized name.
            var isLiveTv = _tv.CurrentApp == "Live TV" || AppName(_activePackage, _activeClassName) == "Live TV";
            _tv.CurrentInput = isLiveTv ? "TV tuner" : IsHdmiActivity() ? "External input (HDMI)" : "Smart TV app";
            _tv.PlaybackStatus = _tv.CurrentApp is "YouTube" or "Netflix" or "Prime Video" or "Disney+" or "Spotify"
                ? "App open · playback status unavailable"
                : isLiveTv ? "Watching live TV" : "Unavailable";
            _tv.CurrentChannel = isLiveTv ? await ReadCurrentChannelApi6Async(ct) ?? "Channel not reported" : "Unavailable";
        }
        else if (sourceJson is not null || channelJson is not null)
        {
            // API 1 TVs report sources and channels directly instead of Android activities.
            var sourceId = ReadString(sourceJson, "id") ?? "";
            _tv.CurrentInput = sourceId.Length > 0 ? FriendlySourceName(sourceId) : "Unavailable";
            _tv.CurrentApp = sourceId.Equals("tv", StringComparison.OrdinalIgnoreCase) ? "Live TV" : _tv.CurrentInput;
            _tv.CurrentActivity = "Reported by JointSPACE 1";
            _tv.PlaybackStatus = "Unavailable";
            var channelId = ReadString(channelJson, "id");
            _tv.CurrentChannel = channelId is not null && _channelIndex?.TryGetValue(channelId, out var known) == true
                ? $"{known.Preset} · {known.Name}"
                : channelId ?? "Unavailable";
        }
        else
        {
            _tv.CurrentApp = "Unavailable";
            _tv.CurrentActivity = _tv.PowerStateKnown && !_tv.IsPoweredOn ? "TV is in standby" : "No app detected";
            _tv.CurrentInput = "Unavailable";
            _tv.CurrentChannel = "Unavailable";
            _tv.PlaybackStatus = "Unavailable";
        }
        var ambiJson = await ambilight;
        if (ambiJson is not null)
        {
            var on = ReadString(ambiJson, "power");
            if (on is not null)
            {
                _tv.AmbilightKnown = true;
                _tv.AmbilightOn = on.Equals("on", StringComparison.OrdinalIgnoreCase);
            }
        }
        var ambiConfig = await ambilightConfig;
        if (ambiConfig is not null)
        {
            var style = ReadString(ambiConfig, "styleName") ?? ReadString(ambiConfig, "current") ?? "";
            var setting = ReadString(ambiConfig, "menuSetting") ?? "";
            if (ReadBool(ambiConfig, "isExpert") == true && style == "FOLLOW_COLOR") setting = "CUSTOM";
            _tv.AmbilightStyle = setting.Length > 0 ? setting : style;
            _tv.AmbilightMode = FriendlyAmbilightName(style, setting);
        }
        var endpointCount = new[] { systemJson, volumeJson, powerJson, activityJson, appJson, ambiJson, ambiConfig, sourceJson, channelJson }.Count(x => x is not null);
        // Every protected endpoint answering 401 while /system still works means the TV forgot this PC.
        _tv.AuthFailed = !string.IsNullOrEmpty(_tv.Username) && _unauthorizedCount >= 2 && volumeJson is null && powerJson is null;
        _tv.IsOnline = endpointCount > 0;
        if (!_tv.IsOnline)
        {
            // Don't keep showing "On" for a TV that stopped answering.
            _tv.PowerStateKnown = false;
            _tv.AmbilightKnown = false;
        }
        if (_tv.IsOnline) _tv.LastSeen = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _tv.StatusMessage = _tv.AuthFailed
            ? "The TV no longer accepts this PC's pairing. Pair again to restore control."
            : _tv.IsOnline ? $"Last updated at {DateTime.Now:HH:mm:ss}" : "TV is not responding";
    }

    private bool IsHdmiActivity()
    {
        var value = (_activePackage + " " + _activeClassName + " " + _activeReportedLabel).ToLowerInvariant();
        return value.Contains("hdmi") || value.Contains("passthrough") || value.Contains("externalsource");
    }

    private async Task<string?> ReadCurrentChannelApi6Async(CancellationToken ct)
    {
        var json = await TryGet("activities/tv", ct);
        var channel = GetProperty(json, "channel");
        var name = ReadString(channel, "name");
        var preset = ReadString(channel, "preset");
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(preset)) return null;
        return string.IsNullOrWhiteSpace(preset) ? name : $"{preset} · {name}";
    }

    public async Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken ct = default)
    {
        var channels = new List<TvChannel>();
        if (IsApi6)
        {
            var db = await GetJsonAsync("channeldb/tv", ct);
            var lists = GetProperty(db, "channelLists") as JsonArray;
            var list = lists?.FirstOrDefault(l => (ReadString(l, "id") ?? "").StartsWith("all", StringComparison.OrdinalIgnoreCase)) ?? lists?.FirstOrDefault();
            var listId = ReadString(list, "id") ?? "all";
            var content = await GetJsonAsync($"channeldb/tv/channelLists/{Uri.EscapeDataString(listId)}", ct);
            var version = ReadString(content, "version") ?? ReadString(list, "version") ?? "";
            if ((GetProperty(content, "Channel") ?? GetProperty(content, "channels")) is JsonArray array)
                foreach (var node in array)
                    channels.Add(new TvChannel
                    {
                        Ccid = ReadString(node, "ccid") ?? "",
                        Preset = ReadString(node, "preset") ?? "",
                        Name = (ReadString(node, "name") ?? "").Trim(),
                        ListId = listId,
                        ListVersion = version
                    });
        }
        else if (await GetJsonAsync("channels", ct) is JsonObject map)
        {
            foreach (var (id, node) in map)
                channels.Add(new TvChannel { Id = id, Preset = ReadString(node, "preset") ?? "", Name = (ReadString(node, "name") ?? id).Trim() });
            _channelIndex = channels.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        }
        return channels.Where(c => c.Name.Length > 0).OrderBy(c => int.TryParse(c.Preset, out var n) ? n : int.MaxValue).ThenBy(c => c.Name).ToList();
    }

    public Task SwitchChannelAsync(TvChannel channel, CancellationToken ct = default) => IsApi6
        ? PostJsonAsync("activities/tv", new JsonObject
        {
            ["channel"] = new JsonObject { ["ccid"] = int.TryParse(channel.Ccid, out var ccid) ? ccid : channel.Ccid, ["preset"] = channel.Preset, ["name"] = channel.Name },
            ["channelList"] = new JsonObject { ["id"] = channel.ListId, ["version"] = channel.ListVersion }
        }, ct)
        : PostJsonAsync("channels/current", new JsonObject { ["id"] = channel.Id }, ct);

    public async Task<IReadOnlyList<TvSource>> GetSourcesAsync(CancellationToken ct = default)
    {
        if (IsApi6 || await GetJsonAsync("sources", ct) is not JsonObject map) return [];
        return map.Select(entry => new TvSource { Id = entry.Key, Name = ReadString(entry.Value, "name") ?? FriendlySourceName(entry.Key) }).ToList();
    }

    public Task SelectSourceAsync(TvSource source, CancellationToken ct = default) =>
        PostJsonAsync("sources/current", new JsonObject { ["id"] = source.Id }, ct);

    public Task SendKeyAsync(string key, CancellationToken ct = default) =>
        PostJsonAsync("input/key", new JsonObject { ["key"] = key }, ct);

    public Task SetVolumeAsync(int volume, CancellationToken ct = default) =>
        PostJsonAsync("audio/volume", new JsonObject { ["current"] = Math.Clamp(volume, 0, Math.Max(1, _tv.MaxVolume)), ["muted"] = false }, ct);

    /// <summary>Android TVs in network standby wake through the powerstate endpoint; fall back to the Standby key.</summary>
    public async Task SetPowerAsync(bool on, CancellationToken ct = default)
    {
        try { await PostJsonAsync("powerstate", new JsonObject { ["powerstate"] = on ? "On" : "Standby" }, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is not HttpStatusCode.Unauthorized) { await SendKeyAsync("Standby", ct); }
    }

    public Task SetAmbilightAsync(bool on, CancellationToken ct = default) =>
        PostJsonAsync("ambilight/power", new JsonObject { ["power"] = on ? "On" : "Off" }, ct);

    public Task SetAmbilightStyleAsync(AmbilightStyle style, CancellationToken ct = default) =>
        PostJsonAsync("ambilight/currentconfiguration", new JsonObject { ["styleName"] = style.StyleName, ["isExpert"] = false, ["menuSetting"] = style.MenuSetting }, ct);

    /// <summary>Sets a fixed color. Tries the classic manual/cached endpoints first, then the Android "expert" color configuration.</summary>
    public async Task SetAmbilightColorAsync(byte r, byte g, byte b, CancellationToken ct = default)
    {
        try
        {
            await PostJsonAsync("ambilight/mode", new JsonObject { ["current"] = "manual" }, ct);
            await PostJsonAsync("ambilight/cached", new JsonObject { ["r"] = r, ["g"] = g, ["b"] = b }, ct);
            return;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not HttpStatusCode.Unauthorized && IsApi6) { }
        var (hue, saturation, value) = ToHsv(r, g, b);
        await PostJsonAsync("ambilight/currentconfiguration", new JsonObject
        {
            ["styleName"] = "FOLLOW_COLOR",
            ["isExpert"] = true,
            ["menuSetting"] = "HOT_LAVA",
            ["colorSettings"] = new JsonObject
            {
                ["color"] = new JsonObject { ["hue"] = (int)Math.Round(hue / 360.0 * 255), ["saturation"] = (int)Math.Round(saturation * 255), ["brightness"] = (int)Math.Round(value * 255) },
                ["colorDelta"] = new JsonObject { ["hue"] = 0, ["saturation"] = 0, ["brightness"] = 0 },
                ["speed"] = 255
            }
        }, ct);
    }

    public Task LaunchApplicationAsync(TvApplication app, CancellationToken ct = default)
    {
        var intent = string.IsNullOrWhiteSpace(app.IntentJson) ? null : JsonNode.Parse(app.IntentJson) as JsonObject;
        if (intent is null)
        {
            intent = new JsonObject();
            if (!string.IsNullOrWhiteSpace(app.Action)) intent["action"] = app.Action;
            if (!string.IsNullOrWhiteSpace(app.PackageName) || !string.IsNullOrWhiteSpace(app.ClassName))
            {
                var component = new JsonObject();
                if (!string.IsNullOrWhiteSpace(app.PackageName)) component["packageName"] = app.PackageName;
                if (!string.IsNullOrWhiteSpace(app.ClassName)) component["className"] = app.ClassName;
                intent["component"] = component;
            }
        }
        return PostJsonAsync("activities/launch", new JsonObject { ["id"] = app.Id, ["order"] = app.Order, ["label"] = app.Label, ["intent"] = intent }, ct);
    }

    private async Task<JsonNode?> TryGet(string endpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await GetJsonAsync(endpoint, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) { Interlocked.Increment(ref _unauthorizedCount); return null; }
        catch { return null; }
    }

    private static TvApplication ParseApplication(JsonNode? node, int index)
    {
        var intent = GetProperty(node, "intent");
        var component = GetProperty(intent, "component") ?? GetProperty(node, "component");
        var rawIntent = intent is JsonValue ? intent.ToString() : "";
        var package = ReadString(component, "packageName") ?? ReadString(node, "packageName") ?? ReadString(node, "package") ?? ParseIntentValue(rawIntent, "pkg") ?? "";
        var className = ReadString(component, "className") ?? ReadString(node, "className") ?? ParseIntentComponent(rawIntent, package) ?? "";
        var reportedLabel = ReadString(node, "label") ?? ReadString(node, "name") ?? ReadString(node, "title");
        var label = string.IsNullOrWhiteSpace(reportedLabel) || LooksLikeTechnicalName(reportedLabel, package, className)
            ? FriendlyApplicationName(package, className, index)
            : reportedLabel.Trim();
        var appId = ReadString(node, "id") ??
            (!string.IsNullOrWhiteSpace(className) || !string.IsNullOrWhiteSpace(package) ? $"{className}-{package}" : $"application-{index}");
        var action = ReadString(intent, "action") ?? ReadString(node, "action") ?? rawIntent;
        var category = ReadString(intent, "category") ?? ReadString(node, "category") ?? "android.intent.category.LAUNCHER";
        var intentJson = intent is JsonObject ? intent.ToJsonString() : "";
        if (intentJson.Length == 0 && (package.Length > 0 || className.Length > 0 || action.Length > 0))
        {
            var fallbackIntent = new JsonObject();
            if (action.Length > 0) fallbackIntent["action"] = action;
            if (package.Length > 0 || className.Length > 0)
            {
                var fallbackComponent = new JsonObject();
                if (package.Length > 0) fallbackComponent["packageName"] = package;
                if (className.Length > 0) fallbackComponent["className"] = className;
                fallbackIntent["component"] = fallbackComponent;
            }
            intentJson = fallbackIntent.ToJsonString();
        }
        return new TvApplication
        {
            Label = label,
            Id = appId,
            Order = ReadInt(node, "order") ?? index - 1,
            PackageName = package,
            ClassName = className,
            Action = action,
            Category = category,
            IntentJson = intentJson
        };
    }

    private static StringContent JsonContent(JsonNode node) => new(node.ToJsonString(), Encoding.UTF8, "application/json");
    private static string RandomString(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return string.Create(length, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    private static (double Hue, double Saturation, double Value) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        var hue = delta == 0 ? 0 : max == rf ? 60 * ((gf - bf) / delta % 6) : max == gf ? 60 * ((bf - rf) / delta + 2) : 60 * ((rf - gf) / delta + 4);
        return (hue < 0 ? hue + 360 : hue, max == 0 ? 0 : delta / max, max);
    }

    public static string FriendlyAmbilightName(string style, string setting)
    {
        if (setting == "CUSTOM") return "Custom color";
        var known = AmbilightStyles.FirstOrDefault(s => s.MenuSetting.Equals(setting, StringComparison.OrdinalIgnoreCase));
        var group = style.ToUpperInvariant() switch
        {
            "FOLLOW_VIDEO" or "INTERNAL" => "Follow video",
            "FOLLOW_AUDIO" => "Follow audio",
            "FOLLOW_COLOR" or "MANUAL" => "Follow color",
            "OFF" => "Off",
            "" => "",
            _ => ToReadableName(style.ToLowerInvariant())
        };
        var detail = known?.Label ?? (setting.Length > 0 ? ToReadableName(setting.ToLowerInvariant()) : "");
        return string.Join(" · ", new[] { group, detail }.Where(s => s.Length > 0)) is { Length: > 0 } label ? label : "Unavailable";
    }

    private static string FriendlySourceName(string id) => id.ToLowerInvariant() switch
    {
        "tv" => "TV tuner",
        "sat" => "Satellite",
        var s when s.StartsWith("hdmi") => "HDMI " + s[4..],
        var s when s.StartsWith("ext") => "EXT " + s[3..],
        var s => ToReadableName(s)
    };

    public static string? ReadString(JsonNode? node, string key) => GetProperty(node, key)?.ToString();
    private static int? ReadInt(JsonNode? node, string key) => int.TryParse(ReadString(node, key), out var v) ? v : null;
    private static bool? ReadBool(JsonNode? node, string key) => bool.TryParse(ReadString(node, key), out var v) ? v : null;

    private static JsonNode? GetProperty(JsonNode? node, string key)
    {
        if (node is not JsonObject obj) return null;
        if (obj.TryGetPropertyValue(key, out var exact)) return exact;
        return obj.FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string? FindStringProperty(JsonNode? node, string key)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj)
            {
                if (entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && entry.Value is JsonValue)
                    return entry.Value.ToString();
                var nested = FindStringProperty(entry.Value, key);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                var nested = FindStringProperty(item, key);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static JsonArray? FindApplicationArray(JsonNode? node)
    {
        if (node is JsonArray rootArray) return rootArray;
        if (node is not JsonObject obj) return null;
        foreach (var entry in obj)
            if (entry.Key.Equals("applications", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Equals("application", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Equals("apps", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Equals("items", StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                if (entry.Value is JsonArray array) return array;
                var nestedArray = FindApplicationArray(entry.Value);
                if (nestedArray is not null) return nestedArray;
            }
        foreach (var entry in obj)
            if (entry.Value is JsonObject nested)
            {
                var nestedArray = FindApplicationArray(nested);
                if (nestedArray is not null) return nestedArray;
            }
        return null;
    }

    private static string? ParseIntentValue(string intent, string key)
    {
        var match = Regex.Match(intent, $@"\b{Regex.Escape(key)}=([^\s\]}}]+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ParseIntentComponent(string intent, string package)
    {
        var match = Regex.Match(intent, @"\bcmp=([^/\s\]}]+)/([^\s\]}]+)");
        if (!match.Success) return null;
        var componentPackage = match.Groups[1].Value;
        var className = match.Groups[2].Value;
        if (!string.IsNullOrWhiteSpace(componentPackage) && string.IsNullOrWhiteSpace(package)) package = componentPackage;
        return className.StartsWith('.') ? package + className : className;
    }

    private static string AppName(string package, string className)
    {
        if (IsUnknownValue(package)) package = "";
        if (IsUnknownValue(className)) className = "";
        var value = (package + " " + className).ToLowerInvariant();
        if (value.Contains("youtube")) return "YouTube";
        if (value.Contains("netflix")) return "Netflix";
        if (value.Contains("primevideo") || value.Contains("amazon")) return "Prime Video";
        if (value.Contains("disney")) return "Disney+";
        if (value.Contains("spotify")) return "Spotify";
        if (value.Contains("tvapp") || value.Contains("livetv") || value.Contains("playtv") || value.Contains("uk.co.freeview.onnow") ||
            package.Equals("org.droidtv.zapster", StringComparison.OrdinalIgnoreCase) ||
            (value.Contains("org.droidtv.tv") && !value.Contains("settings"))) return "Live TV";
        if (!string.IsNullOrWhiteSpace(package))
            return ToReadableName(package.Split('.').LastOrDefault() is { Length: > 0 } tail ? tail : package);
        if (!string.IsNullOrWhiteSpace(className))
            return ToReadableName(className.Split('.', '+').LastOrDefault(part => part.Length > 0) ?? className);
        return "Unavailable";
    }

    private static string GetDisplayAppName(TvApplication? app, string package, string className, string reportedLabel)
    {
        if (app is not null && !string.IsNullOrWhiteSpace(app.Label) &&
            !IsUnknownValue(app.Label) && !LooksLikeTechnicalName(app.Label, package, className))
            return app.Label.Trim();
        if (!string.IsNullOrWhiteSpace(reportedLabel) && !IsUnknownValue(reportedLabel) &&
            !LooksLikeTechnicalName(reportedLabel, package, className))
            return reportedLabel.Trim();
        return AppName(package, className);
    }

    private static bool IsUnknownValue(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().ToUpperInvariant() is "NA" or "N/A" or "UNKNOWN" or "UNAVAILABLE" or "NONE" or "NULL";

    private static bool LooksLikeTechnicalName(string label, string package, string className) =>
        label.Equals(package, StringComparison.OrdinalIgnoreCase) ||
        label.Equals(className, StringComparison.OrdinalIgnoreCase) ||
        label.Contains("PhilipsControl.Models.TvApplication", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyApplicationName(string package, string className, int index)
    {
        var commonName = AppName(package, className);
        if (commonName != "Unavailable") return commonName;
        var candidate = package.Split('.').LastOrDefault(segment =>
            segment.Length > 0 && !new[] { "android", "tv", "app", "application", "client", "launcher" }
                .Contains(segment, StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = className.Split('.', '+').LastOrDefault(segment => segment.Length > 0);
        return string.IsNullOrWhiteSpace(candidate) ? $"Application {index}" : ToReadableName(candidate);
    }

    private static string ToReadableName(string value)
    {
        var name = Regex.Replace(value, "([a-z0-9])([A-Z])", "$1 $2");
        name = Regex.Replace(name.Replace('_', ' ').Replace('-', ' '), @"\s+", " ").Trim();
        if (name.Length == 0) return "Unavailable";
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    public void Dispose() => _http.Dispose();
}

public sealed class PhilipsDiscovery
{
    private static readonly int[] JointSpacePorts = [1926, 1925];

    /// <summary>
    /// SSDP plus a subnet sweep. Each host gets a cheap TCP connect test on the JointSPACE ports first,
    /// so only hosts that actually listen are probed over HTTP. Results are reported as soon as they are found.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveryResult>> DiscoverAsync(CancellationToken ct = default, IProgress<(int Done, int Total, string Message)>? progress = null, Action<DiscoveryResult>? found = null)
    {
        progress?.Report((0, 1, "Listening for SSDP announcements..."));
        var ssdpTask = DiscoverSsdpAsync(ct);
        var hosts = GetSubnetHosts();
        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, DiscoveryResult>(StringComparer.OrdinalIgnoreCase);
        var probed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        using var gate = new SemaphoreSlim(64);

        async Task ProbeAsync(string host)
        {
            if (!probed.TryAdd(host, 0)) return;
            await gate.WaitAsync(ct);
            try
            {
                if (!await AnyPortOpenAsync(host, ct)) return;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var result = await PhilipsJointSpaceClient.ProbeHostAsync(host, timeout.Token);
                if (result is not null && results.TryAdd(result.Host, result)) found?.Invoke(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            finally
            {
                gate.Release();
                var current = Math.Min(Interlocked.Increment(ref completed), hosts.Count);
                if (current % 16 == 0 || current == hosts.Count) progress?.Report((current, Math.Max(hosts.Count, 1), $"Scanning {current}/{hosts.Count} addresses..."));
            }
        }

        var sweep = hosts.Select(ProbeAsync).ToList();
        var ssdpHosts = await ssdpTask;
        var ssdpProbes = ssdpHosts.Select(ProbeAsync).ToList();
        await Task.WhenAll(sweep.Concat(ssdpProbes));
        return results.Values.OrderBy(x => x.Name).ThenBy(x => x.Host).ToArray();
    }

    private static async Task<bool> AnyPortOpenAsync(string host, CancellationToken ct)
    {
        foreach (var port in JointSpacePorts)
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(450));
            try
            {
                await client.ConnectAsync(host, port, timeout.Token);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }
        return false;
    }

    private static async Task<HashSet<string>> DiscoverSsdpAsync(CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searches = new[] { "ssdp:all", "urn:schemas-upnp-org:device:MediaRenderer:1", "urn:schemas-upnp-org:service:RenderingControl:1" };
        var multicast = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        var receiveTasks = new List<Task>();
        foreach (var local in GetLocalAddresses())
        {
            try
            {
                var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                udp.JoinMulticastGroup(multicast.Address, local);
                foreach (var target in searches)
                {
                    var request = Encoding.ASCII.GetBytes($"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {target}\r\n\r\n");
                    await udp.SendAsync(request, request.Length, multicast);
                }
                receiveTasks.Add(Receive(udp, found, ct));
            }
            catch { }
        }
        try { await Task.WhenAll(receiveTasks); } catch { }
        return found;
    }

    private static async Task Receive(UdpClient udp, HashSet<string> found, CancellationToken outer)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timer.CancelAfter(TimeSpan.FromSeconds(2.5));
        try
        {
            while (!timer.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(timer.Token);
                var text = Encoding.UTF8.GetString(packet.Buffer);
                var location = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
                var host = location is not null && Uri.TryCreate(location[(location.IndexOf(':') + 1)..].Trim(), UriKind.Absolute, out var uri)
                    ? uri.Host : packet.RemoteEndPoint.Address.ToString();
                lock (found) found.Add(host);
            }
        }
        catch { }
        finally { udp.Dispose(); }
    }

    private static IEnumerable<UnicastIPAddressInformation> GetLocalUnicast()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            // Skip VPN tunnels and loopback: sweeping them is slow and never finds a TV.
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)) continue;
                var ip = unicast.Address.GetAddressBytes();
                if (ip[0] == 169 && ip[1] == 254) continue;
                yield return unicast;
            }
        }
    }

    private static IEnumerable<IPAddress> GetLocalAddresses() => GetLocalUnicast().Select(u => u.Address).Distinct();

    private static List<string> GetSubnetHosts()
    {
        var results = new List<string>();
        foreach (var unicast in GetLocalUnicast())
        {
            var ip = unicast.Address.GetAddressBytes();
            var mask = unicast.IPv4Mask.GetAddressBytes();
            var bits = BitConverter.ToUInt32(mask.Reverse().ToArray());
            var hostBits = 32 - System.Numerics.BitOperations.PopCount(bits);
            if (hostBits > 9) // large/guest networks: keep discovery local to this device's /24
            {
                mask = [255, 255, 255, 0];
                hostBits = 8;
            }
            var network = new byte[4];
            for (var i = 0; i < 4; i++) network[i] = (byte)(ip[i] & mask[i]);
            var count = Math.Min(1 << hostBits, 512);
            var start = BitConverter.ToUInt32(network.Reverse().ToArray());
            for (var offset = 1; offset < count - 1; offset++)
            {
                var address = BitConverter.GetBytes(start + (uint)offset).Reverse();
                var candidate = new IPAddress(address.ToArray()).ToString();
                if (candidate != unicast.Address.ToString()) results.Add(candidate);
            }
        }
        return results.Distinct().ToList();
    }
}
