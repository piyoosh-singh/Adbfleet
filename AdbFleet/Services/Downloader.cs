using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using AdbFleet.Models;

namespace AdbFleet.Services;

/// <summary>Fetches adb and scrcpy from their official sources into the app's data folder.</summary>
public static class Downloader
{
    const string PlatformToolsUrl = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
    const string ScrcpyApi = "https://api.github.com/repos/Genymobile/scrcpy/releases/latest";

    /// <summary>Portable copies keep their tools beside the exe; installed copies use the data folder.</summary>
    static string ToolRoot => Fleet.IsPortable ? Fleet.AppDir.TrimEnd(Path.DirectorySeparatorChar) : Fleet.DataDir;

    static HttpClient NewClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AdbFleet/1.0");
        return c;
    }

    public static async Task<string> PlatformTools(IProgress<string> progress)
    {
        using var http = NewClient();
        progress.Report("Downloading platform-tools from Google");
        var zip = Path.Combine(Path.GetTempPath(), "adbfleet-platform-tools.zip");
        await using (var s = await http.GetStreamAsync(PlatformToolsUrl))
        await using (var f = File.Create(zip))
            await s.CopyToAsync(f);

        progress.Report("Extracting");
        // Stop the running server first or adb.exe stays locked.
        if (Fleet.Adb.AdbPath is { } old && old.StartsWith(ToolRoot, StringComparison.OrdinalIgnoreCase))
            try { await Fleet.Adb.RunAsync(null, ["kill-server"], default, 10_000); } catch { }
        var target = Path.Combine(ToolRoot, "platform-tools");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        ZipFile.ExtractToDirectory(zip, ToolRoot, true);
        File.Delete(zip);
        var adb = Path.Combine(target, "adb.exe");
        if (!File.Exists(adb)) throw new InvalidOperationException("The download did not contain adb.exe.");
        // A portable copy finds its bundled adb by folder; an absolute path would break on another drive letter.
        Fleet.Settings.Value.AdbPath = Fleet.IsPortable ? "" : adb;
        Fleet.Settings.Save();
        ToolLocator.Reset();
        Fleet.Log.Success(null, $"Installed platform-tools to {target}");
        return adb;
    }

    public static async Task<string> Scrcpy(IProgress<string> progress)
    {
        using var http = NewClient();
        progress.Report("Looking up the latest scrcpy release");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(ScrcpyApi));
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
        string? url = null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (name.StartsWith("scrcpy-win64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                url = a.GetProperty("browser_download_url").GetString();
        }
        if (url == null) throw new InvalidOperationException($"No Windows build found in scrcpy {tag}.");

        progress.Report($"Downloading scrcpy {tag}");
        var zip = Path.Combine(Path.GetTempPath(), "adbfleet-scrcpy.zip");
        await using (var s = await http.GetStreamAsync(url))
        await using (var f = File.Create(zip))
            await s.CopyToAsync(f);

        progress.Report("Extracting");
        var target = Path.Combine(ToolRoot, "scrcpy");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        ZipFile.ExtractToDirectory(zip, target, true);
        File.Delete(zip);
        var exe = Directory.EnumerateFiles(target, "scrcpy.exe", SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException("The download did not contain scrcpy.exe.");
        Fleet.Settings.Value.ScrcpyPath = Fleet.IsPortable ? "" : exe;
        Fleet.Settings.Save();
        Fleet.Log.Add(null, $"Installed scrcpy {tag} to {Path.GetDirectoryName(exe)}", LogKind.Success);
        return exe;
    }
}
