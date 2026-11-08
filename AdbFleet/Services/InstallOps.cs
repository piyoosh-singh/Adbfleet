using System.IO.Compression;
using AdbFleet.Models;

namespace AdbFleet.Services;

public sealed record InstallOptions(bool Replace, bool Downgrade, bool GrantAll, bool BypassLowTargetSdk, bool NoStreaming, bool AllUsers);

/// <summary>One unit of installation: a single APK, or the APKs of one split app plus optional OBB data.</summary>
public sealed record InstallUnit(string Name, List<string> Apks, List<(string local, string remote)> Obbs);

public static class InstallOps
{
    /// <summary>
    /// Turns the queued files into install units. Bundles are unpacked once on the PC and then
    /// installed on every device in parallel.
    /// </summary>
    public static List<InstallUnit> Prepare(IReadOnlyList<QueuedFile> files, bool apksAreOneApp)
    {
        var units = new List<InstallUnit>();
        var loose = files.Where(f => !f.IsBundle).Select(f => f.Path).ToList();
        if (loose.Count > 0)
        {
            if (apksAreOneApp && loose.Count > 1)
                units.Add(new InstallUnit(Path.GetFileName(loose[0]) + $" + {loose.Count - 1} splits", loose, new()));
            else
                units.AddRange(loose.Select(p => new InstallUnit(Path.GetFileName(p), [p], new())));
        }

        foreach (var b in files.Where(f => f.IsBundle))
            units.Add(Unpack(b.Path));
        return units;
    }

    static InstallUnit Unpack(string bundle)
    {
        var dir = Path.Combine(Path.GetTempPath(), "AdbFleet", "bundles", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        var apks = new List<string>();
        var obbs = new List<(string, string)>();
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(bundle); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{Path.GetFileName(bundle)} is not a readable bundle ({ex.Message}). Encrypted APKM files can't be installed.");
        }
        using (zip)
        {
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                var name = e.FullName.Replace('\\', '/');
                if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(dir, $"{apks.Count:00}_{Path.GetFileName(name)}");
                    e.ExtractToFile(dest, true);
                    apks.Add(dest);
                }
                else if (name.EndsWith(".obb", StringComparison.OrdinalIgnoreCase))
                {
                    var dest = Path.Combine(dir, Path.GetFileName(name));
                    e.ExtractToFile(dest, true);
                    // XAPK stores OBBs as Android/obb/<package>/<file>.obb
                    var rel = name.Contains("Android/obb/", StringComparison.OrdinalIgnoreCase)
                        ? name[name.IndexOf("Android/obb/", StringComparison.OrdinalIgnoreCase)..]
                        : "Android/obb/" + Path.GetFileName(name);
                    obbs.Add((dest, "/sdcard/" + rel));
                }
            }
        }
        if (apks.Count == 0) throw new InvalidOperationException($"{Path.GetFileName(bundle)} contains no APK files.");
        // Base APK first keeps older package managers happy.
        apks = apks.OrderBy(p => Path.GetFileName(p).Contains("base", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        return new InstallUnit(Path.GetFileName(bundle), apks, obbs);
    }

    public static async Task<string?> Install(Device d, InstallUnit unit, InstallOptions o)
    {
        var args = new List<string> { unit.Apks.Count > 1 ? "install-multiple" : "install" };
        if (o.Replace) args.Add("-r");
        if (o.Downgrade) args.Add("-d");
        if (o.GrantAll && d.Sdk is 0 or >= 23) args.Add("-g");
        if (o.BypassLowTargetSdk && d.Sdk >= 34) args.Add("--bypass-low-target-sdk-block");
        if (o.NoStreaming) args.Add("--no-streaming");
        if (!o.AllUsers && d.Sdk is 0 or >= 17) { args.Add("--user"); args.Add(Fleet.User.ToString()); }
        args.AddRange(unit.Apks);

        var r = await Fleet.ExecWithTimeout(d, 30 * 60_000, args.ToArray());
        var ok = r.Text.Contains("Success", StringComparison.Ordinal) && !r.Text.Contains("Failure", StringComparison.Ordinal);
        if (!ok)
        {
            var reason = r.Text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("Failure") || l.Contains("error", StringComparison.OrdinalIgnoreCase))
                         ?? r.FirstLine;
            return $"{unit.Name}: {Explain(reason)}";
        }

        foreach (var (local, remote) in unit.Obbs)
        {
            var dir = remote[..remote.LastIndexOf('/')];
            await Fleet.Exec(d, "shell", $"mkdir -p \"{dir}\"");
            var pr = await Fleet.ExecWithTimeout(d, 30 * 60_000, "push", local, remote);
            if (pr.ExitCode != 0) return $"{unit.Name}: installed, but copying game data failed ({pr.FirstLine})";
        }
        return null;
    }

    /// <summary>Adds a plain-language hint to the most common package manager failures.</summary>
    static string Explain(string reason)
    {
        string? hint = reason switch
        {
            _ when reason.Contains("INSTALL_FAILED_VERSION_DOWNGRADE") => "a newer version is installed. Turn on \"Allow downgrade\".",
            _ when reason.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE") => "signed with a different key than the installed app. Uninstall it first.",
            _ when reason.Contains("INSTALL_FAILED_DEPRECATED_SDK_VERSION") => "targets an old Android version. Turn on \"Allow old target SDK\".",
            _ when reason.Contains("INSTALL_FAILED_NO_MATCHING_ABIS") => "built for a different CPU type than this device.",
            _ when reason.Contains("INSTALL_FAILED_INSUFFICIENT_STORAGE") => "not enough free storage on the device.",
            _ when reason.Contains("INSTALL_FAILED_OLDER_SDK") => "needs a newer Android version than this device has.",
            _ when reason.Contains("INSTALL_FAILED_ALREADY_EXISTS") => "already installed. Turn on \"Replace existing\".",
            _ when reason.Contains("INSTALL_FAILED_USER_RESTRICTED") => "installs over USB are blocked. On Xiaomi devices enable \"Install via USB\" in Developer options.",
            _ when reason.Contains("INSTALL_FAILED_MISSING_SPLIT") => "part of a split app is missing. Install all of its APKs together.",
            _ => null,
        };
        return hint == null ? reason : $"{reason} ({hint})";
    }
}
