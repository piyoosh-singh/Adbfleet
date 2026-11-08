using System.Text.RegularExpressions;
using AdbFleet.Models;

namespace AdbFleet.Services;

public static class PackageOps
{
    /// <summary>Packages whose removal usually soft-bricks a device. Actions on them ask first.</summary>
    public static readonly HashSet<string> Critical = new(StringComparer.Ordinal)
    {
        "android", "com.android.systemui", "com.android.settings", "com.android.phone",
        "com.android.providers.settings", "com.android.providers.telephony", "com.android.providers.media",
        "com.android.packageinstaller", "com.google.android.packageinstaller", "com.android.permissioncontroller",
        "com.google.android.permissioncontroller", "com.android.shell", "com.android.server.telecom",
        "com.android.inputmethod.latin", "com.google.android.inputmethod.latin", "com.android.launcher3",
        "com.google.android.gms", "com.google.android.gsf", "com.android.vending", "com.android.externalstorage",
        "com.android.documentsui", "com.android.bluetooth", "com.android.nfc", "com.android.networkstack",
        "com.google.android.networkstack", "com.android.tv.settings", "com.google.android.tvlauncher",
        "com.miui.home", "com.miui.securitycenter", "com.sec.android.app.launcher", "com.android.keychain",
        "com.android.location.fused", "com.android.se", "com.android.wifi.resources",
    };

    static readonly string[] SystemPrefixes = ["/system/", "/product/", "/vendor/", "/system_ext/", "/apex/", "/odm/", "/oem/", "/preload/"];

    static Dictionary<string, string> ParseList(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("package:")) continue;
            line = line["package:".Length..];
            // With -f the line is "path=package"; the path itself may contain '='.
            var eq = line.LastIndexOf('=');
            if (eq > 0) map[line[(eq + 1)..]] = line[..eq];
            else map[line] = "";
        }
        return map;
    }

    public static async Task<List<AppInfo>> ListAsync(Device d)
    {
        var u = Fleet.User;
        var everyT = Fleet.Adb.ShellAsync(d.Serial, $"pm list packages -f -u --user {u}", default, 60_000);
        var installedT = Fleet.Adb.ShellAsync(d.Serial, $"pm list packages --user {u}", default, 60_000);
        var disabledT = Fleet.Adb.ShellAsync(d.Serial, $"pm list packages -d --user {u}", default, 60_000);
        var systemT = Fleet.Adb.ShellAsync(d.Serial, $"pm list packages -s -u --user {u}", default, 60_000);
        await Task.WhenAll(everyT, installedT, disabledT, systemT);

        var every = ParseList(everyT.Result.StdOut);
        if (every.Count == 0)
        {
            // Very old Android: no --user / -u support.
            var fallback = await Fleet.Adb.ShellAsync(d.Serial, "pm list packages -f", default, 60_000);
            every = ParseList(fallback.StdOut);
            if (every.Count == 0) throw new InvalidOperationException(everyT.Result.FirstLine.Length > 0 ? everyT.Result.FirstLine : "The device returned no packages.");
        }
        var installed = ParseList(installedT.Result.StdOut);
        var disabled = ParseList(disabledT.Result.StdOut);
        var system = ParseList(systemT.Result.StdOut);

        return every.Select(kv => new AppInfo
        {
            Package = kv.Key,
            ApkPath = kv.Value,
            IsSystem = system.ContainsKey(kv.Key) || SystemPrefixes.Any(p => kv.Value.StartsWith(p, StringComparison.Ordinal)),
            IsInstalled = installed.Count == 0 || installed.ContainsKey(kv.Key),
            IsEnabled = !disabled.ContainsKey(kv.Key),
        }).OrderBy(a => a.Package, StringComparer.Ordinal).ToList();
    }

    static string? Check(AdbResult r) => r.Ok ? null : (r.FirstLine.Length > 0 ? r.FirstLine : $"exit code {r.ExitCode}");

    public static async Task<string?> Disable(Device d, string pkg)
        => Check(await Fleet.Exec(d, "shell", $"pm disable-user --user {Fleet.User} {pkg}"));

    public static async Task<string?> Enable(Device d, string pkg)
    {
        var r = await Fleet.Exec(d, "shell", $"pm enable --user {Fleet.User} {pkg}");
        return Check(r);
    }

    public static async Task<string?> Uninstall(Device d, string pkg, bool keepData)
        => Check(await Fleet.Exec(d, "shell", $"pm uninstall{(keepData ? " -k" : "")} --user {Fleet.User} {pkg}"));

    /// <summary>Brings back a package that was removed for the user but still exists on the system partition.</summary>
    public static async Task<string?> Restore(Device d, string pkg)
    {
        var r = await Fleet.Exec(d, "shell", $"cmd package install-existing --user {Fleet.User} {pkg}");
        if (!r.Ok || r.Text.Contains("Unknown command", StringComparison.OrdinalIgnoreCase))
            r = await Fleet.Exec(d, "shell", $"pm install-existing --user {Fleet.User} {pkg}");
        return Check(r);
    }

    public static async Task<string?> ClearData(Device d, string pkg)
        => Check(await Fleet.Exec(d, "shell", $"pm clear --user {Fleet.User} {pkg}"));

    public static async Task<string?> ForceStop(Device d, string pkg)
        => Check(await Fleet.Exec(d, "shell", $"am force-stop {pkg}"));

    public static async Task<string?> Launch(Device d, string pkg)
    {
        var r = await Fleet.Exec(d, "shell", $"monkey -p {pkg} -c android.intent.category.LAUNCHER 1");
        if (r.Text.Contains("No activities found", StringComparison.Ordinal))
            r = await Fleet.Exec(d, "shell", $"monkey -p {pkg} -c android.intent.category.LEANBACK_LAUNCHER 1");
        if (r.Text.Contains("No activities found", StringComparison.Ordinal)) return "the app has no launcher screen";
        return r.ExitCode == 0 ? null : r.FirstLine;
    }

    /// <summary>Copies the APK (and any split APKs) of a package to the PC.</summary>
    public static async Task<string?> SaveApk(Device d, string pkg)
    {
        var paths = await Fleet.Exec(d, "shell", $"pm path --user {Fleet.User} {pkg}");
        var remote = paths.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:"))
            .Select(l => l["package:".Length..]).ToList();
        if (remote.Count == 0)
        {
            paths = await Fleet.Exec(d, "shell", $"pm path {pkg}");
            remote = paths.StdOut.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:"))
                .Select(l => l["package:".Length..]).ToList();
        }
        if (remote.Count == 0) return "could not find the APK on the device";

        var info = await Details(d, pkg);
        var version = info.GetValueOrDefault("Version", "");
        var dir = Path.Combine(Fleet.Settings.DownloadsDir, d.SafeName);
        Directory.CreateDirectory(dir);
        var stem = version.Length > 0 ? $"{pkg} [v{Sanitize(version)}]" : pkg;

        if (remote.Count == 1)
        {
            var dest = Path.Combine(dir, stem + ".apk");
            var r = await Fleet.ExecWithTimeout(d, 600_000, "pull", remote[0], dest);
            return r.ExitCode == 0 && File.Exists(dest) ? null : r.FirstLine;
        }

        // Split app: pull every part into its own folder so it can be reinstalled together.
        var folder = Path.Combine(dir, stem);
        Directory.CreateDirectory(folder);
        foreach (var rp in remote)
        {
            var dest = Path.Combine(folder, Path.GetFileName(rp));
            var r = await Fleet.ExecWithTimeout(d, 600_000, "pull", rp, dest);
            if (r.ExitCode != 0) return r.FirstLine;
        }
        return null;
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    static readonly (string key, Regex rx)[] DetailPatterns =
    [
        ("Version", new Regex(@"versionName=(\S+)")),
        ("Version code", new Regex(@"versionCode=(\d+)")),
        ("Target SDK", new Regex(@"targetSdk=(\d+)")),
        ("Min SDK", new Regex(@"minSdk=(\d+)")),
        ("Installed", new Regex(@"firstInstallTime=([^\r\n]+)")),
        ("Updated", new Regex(@"lastUpdateTime=([^\r\n]+)")),
        ("Installer", new Regex(@"installerPackageName=(\S+)")),
        ("Data folder", new Regex(@"dataDir=(\S+)")),
        ("UID", new Regex(@"userId=(\d+)")),
    ];

    public static async Task<Dictionary<string, string>> Details(Device d, string pkg)
    {
        var r = await Fleet.Adb.ShellAsync(d.Serial, $"dumpsys package {pkg}", default, 30_000);
        var text = r.StdOut;
        // Only the "Packages:" section describes the package itself.
        var idx = text.IndexOf($"Package [{pkg}]", StringComparison.Ordinal);
        if (idx >= 0) text = text[idx..];
        var map = new Dictionary<string, string>();
        foreach (var (key, rx) in DetailPatterns)
        {
            var m = rx.Match(text);
            if (m.Success) map[key] = m.Groups[1].Value.Trim();
        }
        return map;
    }
}
