using System.Globalization;
using System.Text.RegularExpressions;
using AdbFleet.Models;

namespace AdbFleet.Services;

/// <summary>Reads a device's details in one adb round trip and stores them on the device.</summary>
public static partial class InfoOps
{
    const string Sep = "@@ADBFLEET@@";

    // One shell call instead of six: each section is separated by a marker line.
    static readonly string Script = string.Join($"; echo {Sep}; ",
        "wm size", "wm density", "cat /proc/meminfo", "df -k /data", "dumpsys battery", "cat /proc/uptime");

    [GeneratedRegex(@"(\d+x\d+)")] private static partial Regex SizeRx();
    [GeneratedRegex(@"(\d+)")] private static partial Regex NumRx();

    /// <summary>Loads (or reloads) details. Returns null on success or an error message.</summary>
    public static async Task<string?> Load(Device d)
    {
        if (!d.IsOnline) return "not connected";
        await Fleet.OnUI(() => d.InfoLoading = true);
        try
        {
            var r = await Fleet.Adb.ShellAsync(d.Serial, Script, default, 30_000);
            if (r.ExitCode != 0 && r.StdOut.Length == 0) return r.FirstLine;
            var info = Parse(d, r.StdOut);
            await Fleet.OnUI(() => d.Info = info);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
        finally { await Fleet.OnUI(() => d.InfoLoading = false); }
    }

    public static Task LoadAll(IEnumerable<Device> devices)
        => Task.WhenAll(devices.Where(d => d.IsOnline).Select(Load));

    static DeviceInfo Parse(Device d, string output)
    {
        var parts = output.Replace("\r", "").Split(Sep);
        string Part(int i) => i < parts.Length ? parts[i] : "";
        string P(string k) => d.Props.GetValueOrDefault(k, "");

        // wm size / wm density: "Physical size: 1920x1080" and optionally "Override size: ..."
        (string phys, string over) TwoLines(string text, Regex rx)
        {
            string phys = "", over = "";
            foreach (var line in text.Split('\n'))
            {
                var m = rx.Match(line);
                if (!m.Success) continue;
                if (line.Contains("Override", StringComparison.OrdinalIgnoreCase)) over = m.Groups[1].Value;
                else phys = m.Groups[1].Value;
            }
            return (phys, over);
        }
        var (size, sizeOver) = TwoLines(Part(0), SizeRx());
        var (dens, densOver) = TwoLines(Part(1), NumRx());

        var mem = Part(2);
        long Kb(string key) { var m = Regex.Match(mem, key + @":\s+(\d+)"); return m.Success ? long.Parse(m.Groups[1].Value) : 0; }

        long storTotal = 0, storFree = 0;
        var dfLines = Part(3).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Filesystem")).ToList();
        if (dfLines.Count > 0)
        {
            // Long filesystem names can wrap onto their own line; the numbers are on the last line.
            var cols = Regex.Split(dfLines[^1], @"\s+").Where(c => c.Length > 0).ToArray();
            var nums = cols.Where(c => long.TryParse(c, out _)).Select(long.Parse).ToArray();
            if (nums.Length >= 3) { storTotal = nums[0]; storFree = nums[2]; }
        }

        var bat = Part(4);
        var present = !bat.Contains("present: false");
        var lvl = Regex.Match(bat, @"level:\s*(\d+)");
        var charging = Regex.IsMatch(bat, @"(AC|USB|Wireless) powered:\s*true") && Regex.Match(bat, @"status:\s*(\d)").Groups[1].Value is "2" or "5";
        // Many TV boxes report a fake battery (e.g. 42%, 4 mV). Real batteries report about 3000-4500 mV.
        var volt = Regex.Match(bat, @"voltage:\s*(\d+)");
        var fake = (volt.Success && int.Parse(volt.Groups[1].Value) < 2000)
                   || P("ro.build.characteristics").Contains("tv", StringComparison.OrdinalIgnoreCase);
        var battery = present && !fake && lvl.Success ? int.Parse(lvl.Groups[1].Value) : -1;

        var up = TimeSpan.Zero;
        var upTok = Part(5).Trim().Split(' ').FirstOrDefault();
        if (double.TryParse(upTok, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)) up = TimeSpan.FromSeconds(secs);

        return new DeviceInfo
        {
            Build = P("ro.build.display.id"),
            Cpu = P("ro.product.cpu.abi"),
            SecurityPatch = P("ro.build.version.security_patch"),
            HardwareSerial = d.HardwareSerial,
            Codename = P("ro.product.device"),
            Screen = size,
            ScreenOverride = sizeOver,
            Density = dens,
            DensityOverride = densOver,
            MemTotalKb = Kb("MemTotal"),
            MemAvailKb = Kb("MemAvailable") is > 0 and var a ? a : Kb("MemFree"),
            StorageTotalKb = storTotal,
            StorageFreeKb = storFree,
            Battery = battery,
            Charging = charging,
            Uptime = up,
            ReadAt = DateTime.Now,
        };
    }
}
