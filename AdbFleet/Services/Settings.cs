using System.Text.Json;
using AdbFleet.Models;

namespace AdbFleet.Services;

public sealed class AppSettings
{
    public string AdbPath { get; set; } = "";
    public string ScrcpyPath { get; set; } = "";
    public string ScrcpyArgs { get; set; } = "--stay-awake";
    public string DownloadsDir { get; set; } = "";
    public string ScreenshotsDir { get; set; } = "";
    public string Theme { get; set; } = "System";
    public int UserId { get; set; } = 0;
    public bool GrantPermissionsOnInstall { get; set; } = false;
    public List<string> ConnectHistory { get; set; } = new();
    public List<string> QuickCommands { get; set; } = new()
    {
        "shell getprop ro.build.version.release",
        "shell dumpsys battery",
        "shell df -h /data",
        "shell settings get secure android_id",
    };
    public List<Preset> Presets { get; set; } = new();
    public List<KnownDevice> KnownDevices { get; set; } = new();
    public bool ReconnectOnStart { get; set; } = true;
    public int ProxyPort { get; set; } = 8888;
}

public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public AppSettings Value { get; private set; } = new();
    string FilePath => Path.Combine(Fleet.DataDir, "settings.json");

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Value = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts) ?? new();
        }
        catch { Value = new(); }
    }

    readonly object _gate = new();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Fleet.DataDir);
            string json;
            lock (_gate) json = JsonSerializer.Serialize(Value, Opts);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex) { Fleet.Log.Add(null, "Could not save settings: " + ex.Message, LogKind.Error); }
    }

    public string DownloadsDir => Ensure(string.IsNullOrWhiteSpace(Value.DownloadsDir)
        ? (Fleet.IsPortable ? Path.Combine(Fleet.AppDir, "downloads")
                            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AdbFleet"))
        : Value.DownloadsDir);

    public string ScreenshotsDir => Ensure(string.IsNullOrWhiteSpace(Value.ScreenshotsDir)
        ? (Fleet.IsPortable ? Path.Combine(Fleet.AppDir, "screenshots")
                            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AdbFleet"))
        : Value.ScreenshotsDir);

    static string Ensure(string d) { Directory.CreateDirectory(d); return d; }

    public void RememberHost(string host)
    {
        var h = Value.ConnectHistory;
        h.RemoveAll(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase));
        h.Insert(0, host);
        if (h.Count > 15) h.RemoveRange(15, h.Count - 15);
        Save();
    }
}

public static class ToolLocator
{
    static string? _adbCache;

    public static void Reset() => _adbCache = null;

    static IEnumerable<string> PathDirs() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);

    public static string? FindAdb()
    {
        if (_adbCache != null && File.Exists(_adbCache)) return _adbCache;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bundled = new[]
        {
            Path.Combine(Fleet.AppDir, "platform-tools", "adb.exe"),
            Path.Combine(Fleet.DataDir, "platform-tools", "adb.exe"),
        };
        var candidates = new List<string>();
        // A portable copy uses the adb it carries, whatever is installed on the host PC.
        if (Fleet.IsPortable) { candidates.AddRange(bundled); candidates.Add(Fleet.Settings.Value.AdbPath); }
        else { candidates.Add(Fleet.Settings.Value.AdbPath); candidates.AddRange(bundled); }
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.AddRange(PathDirs().Select(d => Path.Combine(d.Trim('"'), "adb.exe")));
        _adbCache = candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
        return _adbCache;
    }

    public static string? FindScrcpy()
    {
        var bundled = new List<string>();
        foreach (var dir in new[] { Path.Combine(Fleet.AppDir, "scrcpy"), Path.Combine(Fleet.DataDir, "scrcpy") })
            if (Directory.Exists(dir))
                bundled.AddRange(Directory.EnumerateFiles(dir, "scrcpy.exe", SearchOption.AllDirectories));
        var list = new List<string>();
        if (Fleet.IsPortable) { list.AddRange(bundled); list.Add(Fleet.Settings.Value.ScrcpyPath); }
        else { list.Add(Fleet.Settings.Value.ScrcpyPath); list.AddRange(bundled); }
        list.AddRange(PathDirs().Select(d => Path.Combine(d.Trim('"'), "scrcpy.exe")));
        return list.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }
}
