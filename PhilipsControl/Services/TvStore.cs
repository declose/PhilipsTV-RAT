using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using PhilipsControl.Models;

namespace PhilipsControl.Services;

public sealed class TvStore
{
    // PHILIPSCONTROL_DATA_DIR allows a portable or test setup that leaves the normal profile untouched.
    public static readonly string DataFolder = Environment.GetEnvironmentVariable("PHILIPSCONTROL_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhilipsControl");
    private readonly string _file = Path.Combine(DataFolder, "televisions.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private string _lastPlainState = "";

    public List<TvRecord> Load()
    {
        try
        {
            if (!File.Exists(_file)) return [];
            var records = JsonSerializer.Deserialize<List<TvRecord>>(File.ReadAllText(_file)) ?? [];
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.CredentialBlob)) continue;
                try
                {
                    var pair = Dpapi.Unprotect(Convert.FromBase64String(record.CredentialBlob));
                    var parts = Encoding.UTF8.GetString(pair).Split('\n', 2);
                    if (parts.Length == 2) { record.Username = parts[0]; record.Password = parts[1]; }
                }
                catch { record.CredentialBlob = ""; }
            }
            _lastPlainState = PlainState(records);
            return records;
        }
        catch { return []; }
    }

    /// <summary>Writes the device list, skipping the disk entirely when nothing that is persisted has changed.</summary>
    public void Save(IEnumerable<TvRecord> records)
    {
        var list = records.ToList();
        var state = PlainState(list);
        if (state == _lastPlainState) return;
        Directory.CreateDirectory(DataFolder);
        var snapshots = list.Select(record => new TvRecord
        {
            Name = record.Name,
            Alias = record.Alias,
            Host = record.Host,
            Model = record.Model,
            ApiVersion = record.ApiVersion,
            Port = record.Port,
            UseHttps = record.UseHttps,
            CredentialBlob = string.IsNullOrWhiteSpace(record.Username) ? "" : Convert.ToBase64String(Dpapi.Protect(Encoding.UTF8.GetBytes(record.Username + "\n" + record.Password))),
            MacAddress = record.MacAddress,
            FavoriteApps = [.. record.FavoriteApps],
            LastSeen = record.LastSeen
        }).ToList();
        var tempFile = _file + ".tmp";
        File.WriteAllText(tempFile, JsonSerializer.Serialize(snapshots, Options));
        File.Move(tempFile, _file, true);
        _lastPlainState = state;
    }

    // Fingerprint of everything persisted, compared in memory only (never written).
    private static string PlainState(IEnumerable<TvRecord> records) => string.Join("\u001e", records.Select(r => string.Join("\u001f",
        r.Name, r.Alias, r.Host, r.Model, r.ApiVersion, r.Port, r.UseHttps, r.Username, r.Password, r.MacAddress, string.Join(",", r.FavoriteApps), r.LastSeen)));
}

public sealed class AppSettings
{
    public string Accent { get; set; } = "green";
    public int PollSeconds { get; set; } = 5;
    public bool CloseToTray { get; set; } = true;
    public bool KeyboardShortcuts { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public string ActiveHost { get; set; } = "";
    public string LastPage { get; set; } = "overview";
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 1220;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }
    public bool TrayHintShown { get; set; }

    private static readonly string FilePath = Path.Combine(TvStore.DataFolder, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(TvStore.DataFolder);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, Options));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch { }
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PhilipsControl";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(ValueName, false);
    }
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static byte[] Protect(byte[] plain) => Transform(plain, true);
    public static byte[] Unprotect(byte[] encrypted) => Transform(encrypted, false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        var source = new DataBlob { Size = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Marshal.Copy(input, 0, source.Data, input.Length);
        DataBlob result = default;
        try
        {
            var ok = protect
                ? CryptProtectData(ref source, "Philips Control credentials", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result)
                : CryptUnprotectData(ref source, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out result);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var data = new byte[result.Size];
            Marshal.Copy(result.Data, data, 0, result.Size);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(source.Data);
            if (result.Data != IntPtr.Zero) LocalFree(result.Data);
        }
    }
}
