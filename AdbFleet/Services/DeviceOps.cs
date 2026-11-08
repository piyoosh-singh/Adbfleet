using System.Diagnostics;
using System.Text.RegularExpressions;
using AdbFleet.Models;

namespace AdbFleet.Services;

public static class DeviceOps
{
    public static async Task<string?> Screenshot(Device d, Action<string>? saved = null)
    {
        var file = Path.Combine(Fleet.Settings.ScreenshotsDir, $"{d.SafeName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        Fleet.Log.Add(d, "exec-out screencap -p", LogKind.Command);
        long size = 0;
        try { size = await Fleet.Adb.RunToFileAsync(d.Serial, ["exec-out", "screencap -p"], file); } catch { }
        if (size < 100)
        {
            // Android 4.x has no exec-out: capture on the device and pull.
            const string tmp = "/data/local/tmp/adbfleet_shot.png";
            await Fleet.Exec(d, "shell", $"screencap -p {tmp}");
            await Fleet.Exec(d, "pull", tmp, file);
            await Fleet.Exec(d, "shell", $"rm {tmp}");
            size = File.Exists(file) ? new FileInfo(file).Length : 0;
        }
        if (size < 100) return "the device returned an empty image (secure screens such as DRM video can't be captured)";
        Fleet.Log.Success(d, $"Screenshot saved to {file}");
        saved?.Invoke(file);
        return null;
    }

    public static async Task<string?> Key(Device d, string keycode)
    {
        var r = await Fleet.Exec(d, "shell", $"input keyevent {keycode}");
        return r.ExitCode == 0 ? null : r.FirstLine;
    }

    public static async Task<string?> Text(Device d, string text)
    {
        // input text treats spaces as separators and needs shell metacharacters escaped.
        var escaped = Regex.Replace(text, @"([\\""'`$&|;<>()\[\]{}*?!~#])", @"\$1").Replace(" ", "%s");
        var r = await Fleet.Exec(d, "shell", $"input text \"{escaped}\"");
        return r.ExitCode == 0 ? null : r.FirstLine;
    }

    public static async Task<string?> Reboot(Device d, string mode)
    {
        var r = mode switch
        {
            "poweroff" => await Fleet.Exec(d, "shell", "reboot -p"),
            "" => await Fleet.Exec(d, "reboot"),
            _ => await Fleet.Exec(d, "reboot", mode),
        };
        return r.ExitCode == 0 ? null : r.FirstLine;
    }

    public static async Task<string?> SetScreen(Device d, string? size, string? density)
    {
        if (size != null)
        {
            var r = await Fleet.Exec(d, "shell", size == "reset" ? "wm size reset" : $"wm size {size}");
            if (!r.Ok) return r.FirstLine;
        }
        if (density != null)
        {
            var r = await Fleet.Exec(d, "shell", density == "reset" ? "wm density reset" : $"wm density {density}");
            if (!r.Ok) return r.FirstLine;
        }
        return null;
    }

    /// <summary>Switches a USB device to network debugging and connects to it, keeping the USB link usable.</summary>
    public static async Task<string?> SwitchToWifi(Device d)
    {
        if (d.IsWifi) return "already connected over Wi-Fi";
        var ip = await GetIp(d);
        if (ip == null) return "could not find the device's Wi-Fi address. Make sure Wi-Fi is on.";
        var r = await Fleet.Exec(d, "tcpip", "5555");
        if (r.ExitCode != 0) return r.FirstLine;
        await Task.Delay(2500);
        var host = $"{ip}:5555";
        var c = await Connect(host);
        return c;
    }

    public static async Task<string?> GetIp(Device d)
    {
        foreach (var cmd in new[] { "ip -f inet addr show wlan0", "ip -f inet addr show eth0", "ip route" })
        {
            var r = await Fleet.Adb.ShellAsync(d.Serial, cmd);
            var m = Regex.Match(r.StdOut, @"inet (\d+\.\d+\.\d+\.\d+)/");
            if (!m.Success) m = Regex.Match(r.StdOut, @"src (\d+\.\d+\.\d+\.\d+)");
            if (m.Success && !m.Groups[1].Value.StartsWith("127.")) return m.Groups[1].Value;
        }
        return null;
    }

    public static string NormalizeHost(string host)
    {
        host = host.Trim();
        if (host.Length > 0 && !host.Contains(':') && !host.Contains("._tcp")) host += ":5555";
        return host;
    }

    public static async Task<string?> Connect(string host)
    {
        host = NormalizeHost(host);
        var r = await Fleet.ExecWithTimeout(null, 20_000, "connect", host);
        var t = r.Text;
        if (t.Contains("connected to", StringComparison.Ordinal) && !t.Contains("cannot", StringComparison.OrdinalIgnoreCase) && !t.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            Fleet.Settings.RememberHost(host);
            return null;
        }
        if (t.Contains("failed to authenticate", StringComparison.Ordinal))
        {
            Fleet.Settings.RememberHost(host);
            return "accept the debugging prompt on the device screen, then connect again";
        }
        return t.Length > 0 ? t : "no response from adb";
    }

    public static async Task<string?> Pair(string hostPort, string code)
    {
        var r = await Fleet.ExecWithTimeout(null, 30_000, "pair", hostPort.Trim(), code.Trim());
        return r.Text.Contains("Successfully paired", StringComparison.Ordinal) ? null : (r.Text.Length > 0 ? r.Text : "pairing failed");
    }

    public static async Task<string?> Disconnect(Device d)
    {
        var r = await Fleet.Exec(null, "disconnect", d.Serial);
        return r.ExitCode == 0 ? null : r.FirstLine;
    }

    public sealed record MdnsService(string Name, string Type, string Address)
    {
        public bool IsPairing => Type.Contains("pairing", StringComparison.Ordinal);
        public string Label => $"{Name}  {Address}";
    }

    public static async Task<List<MdnsService>> DiscoverMdns()
    {
        var r = await Fleet.Adb.RunAsync(null, ["mdns", "services"], default, 15_000);
        var list = new List<MdnsService>();
        foreach (var line in r.StdOut.Split('\n'))
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length >= 3 && parts[1].StartsWith("_adb", StringComparison.Ordinal))
                list.Add(new MdnsService(parts[0], parts[1], parts[2]));
        }
        return list;
    }

    public static string? LaunchScrcpy(Device d, string extraArgs)
    {
        var exe = ToolLocator.FindScrcpy();
        if (exe == null) return "scrcpy was not found. Download it from Settings.";
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(d.Serial);
        psi.ArgumentList.Add("--window-title");
        psi.ArgumentList.Add(d.DisplayName);
        foreach (var a in CommandLine.Split(extraArgs)) psi.ArgumentList.Add(a);
        // Make scrcpy reuse our adb so two adb versions don't keep restarting each other's server.
        var adb = Fleet.Adb.AdbPath;
        if (adb != null) psi.Environment["ADB"] = adb;
        Fleet.Log.Add(d, $"scrcpy {AdbClient.Display(psi.ArgumentList)}", LogKind.Info);
        try
        {
            var p = Process.Start(psi)!;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                try { if (p.ExitCode != 0) Fleet.Log.Error(d, $"scrcpy closed with exit code {p.ExitCode}"); } catch { }
            };
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static async Task<List<ProcessRow>> Processes(Device d)
    {
        var r = await Fleet.Adb.ShellAsync(d.Serial, "ps -A -o PID,USER,RSS,NAME", default, 30_000);
        if (r.ExitCode != 0 || r.Text.Contains("bad -o", StringComparison.OrdinalIgnoreCase) || r.StdOut.Split('\n').Length < 3)
            r = await Fleet.Adb.ShellAsync(d.Serial, "ps", default, 30_000);
        var lines = r.StdOut.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        if (lines.Count < 2) return new();
        var header = Regex.Split(lines[0].Trim(), @"\s+").Select(h => h.ToUpperInvariant()).ToList();
        int iPid = header.IndexOf("PID"), iUser = header.IndexOf("USER"), iRss = header.IndexOf("RSS");
        var rows = new List<ProcessRow>();
        foreach (var line in lines.Skip(1))
        {
            var p = Regex.Split(line.Trim(), @"\s+");
            if (p.Length < header.Count) continue;
            int.TryParse(iPid >= 0 ? p[iPid] : "0", out var pid);
            long.TryParse(iRss >= 0 ? p[iRss] : "0", out var rss);
            rows.Add(new ProcessRow { Pid = pid, User = iUser >= 0 ? p[iUser] : "", RssKb = rss, Name = p[^1] });
        }
        return rows.OrderByDescending(x => x.RssKb).ToList();
    }

}
