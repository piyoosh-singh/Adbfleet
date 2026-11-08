using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using AdbFleet.Models;

namespace AdbFleet.Services;

/// <summary>An IPv4 address of this PC, with the adapter it belongs to.</summary>
public sealed record PcAddress(string Ip, string Adapter, IPAddress Mask, bool Virtual)
{
    public override string ToString() => Ip;
    public string Label => $"{Ip}  ({Adapter})";
}

/// <summary>
/// Reads and changes the device-wide HTTP proxy ("settings global http_proxy"). This is the same
/// proxy Android's developer tools and most proxy apps set; it applies on every network.
/// </summary>
public static partial class ProxyOps
{
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();

    static bool IsOff(string v) => v.Length == 0 || v == "null" || v.StartsWith(':');

    /// <summary>Returns "" when no proxy is set, "host:port" when one is, or throws when the device can't be read.</summary>
    public static async Task<string> Read(Device d)
    {
        var r = await Fleet.Adb.ShellAsync(d.Serial,
            "settings get global http_proxy; settings get global global_http_proxy_host; settings get global global_http_proxy_port",
            default, 15_000);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.FirstLine);
        var lines = r.StdOut.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToArray();
        string L(int i) => i < lines.Length ? lines[i] : "";

        var http = L(0);
        if (!IsOff(http)) return http;
        // Global proxy set through a device policy (or older tools) uses separate host/port keys.
        var host = L(1);
        if (IsOff(host)) return "";
        var port = L(2);
        return IsOff(port) ? host : $"{host}:{port}";
    }

    /// <summary>Reads the proxy and stores it on the device. Returns null on success or an error message.</summary>
    public static async Task<string?> Refresh(Device d)
    {
        try
        {
            var p = await Read(d);
            await Fleet.OnUI(() => d.Proxy = p);
            return null;
        }
        catch (Exception ex) { return "could not read the proxy setting: " + ex.Message; }
    }

    /// <summary>Refreshes every online device without logging, e.g. after the availability check.</summary>
    public static Task RefreshAll(IEnumerable<Device> devices)
        => Task.WhenAll(devices.Where(d => d.IsOnline).Select(Refresh));

    public static string? Validate(string host, double port)
    {
        if (host.Length == 0) return "Enter the proxy's IP address or host name.";
        if (!IPAddress.TryParse(host, out _) && !HostPattern().IsMatch(host))
            return $"\"{host}\" isn't a valid IP address or host name.";
        if (double.IsNaN(port) || port < 1 || port > 65535 || port != Math.Floor(port))
            return "The port must be a whole number from 1 to 65535.";
        return null;
    }

    public static async Task<string?> Set(Device d, string host, int port)
    {
        var value = $"{host}:{port}";
        var r = await Fleet.Exec(d, "shell", $"settings put global http_proxy {value}");
        if (r.ExitCode != 0 || !r.Ok) return r.FirstLine.Length > 0 ? r.FirstLine : "the device refused the setting";
        var now = await Verify(d);
        return now == value ? null : $"the device reports {(now is { Length: > 0 } ? now : "no proxy")} after the change";
    }

    public static async Task<string?> Clear(Device d)
    {
        // ":0" switches the proxy off immediately; deleting the key only takes effect after a reboot.
        var r = await Fleet.Exec(d, "shell",
            "settings put global http_proxy :0; " +
            "settings delete global global_http_proxy_host; settings delete global global_http_proxy_port; " +
            "settings delete global global_http_proxy_exclusion_list; settings delete global global_proxy_pac_url");
        if (r.ExitCode != 0) return r.FirstLine;
        var now = await Verify(d);
        return now == "" ? null : $"a proxy is still set ({now}). It may be enforced by a device policy";
    }

    static async Task<string?> Verify(Device d)
    {
        try
        {
            var p = await Read(d);
            await Fleet.OnUI(() => d.Proxy = p);
            return p;
        }
        catch { return null; }
    }

    // ---- This PC's addresses ----

    static readonly string[] VirtualHints = ["vEthernet", "Hyper-V", "VirtualBox", "VMware", "WSL", "Loopback", "Bluetooth", "TAP", "Tailscale", "ZeroTier"];

    /// <summary>IPv4 addresses of connected adapters, real network adapters with a gateway first.</summary>
    public static List<PcAddress> PcAddresses()
    {
        var list = new List<(PcAddress a, bool gw)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var gw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var isVirtual = VirtualHints.Any(h => nic.Name.Contains(h, StringComparison.OrdinalIgnoreCase)
                                                   || nic.Description.Contains(h, StringComparison.OrdinalIgnoreCase));
                foreach (var u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = u.Address.ToString();
                    if (ip.StartsWith("169.254.") || ip.StartsWith("127.")) continue;
                    list.Add((new PcAddress(ip, nic.Name, u.IPv4Mask ?? IPAddress.Parse("255.255.255.0"), isVirtual), gw));
                }
            }
        }
        catch { }
        return list.OrderBy(x => x.a.Virtual).ThenByDescending(x => x.gw).Select(x => x.a).ToList();
    }

    /// <summary>The PC address on the same subnet as most of the devices, or the first real adapter.</summary>
    public static PcAddress? BestFor(IEnumerable<Device> devices, IReadOnlyList<PcAddress> pc)
    {
        if (pc.Count == 0) return null;
        var hosts = devices.Select(d => d.PingHost).Where(h => h != null).Select(h => IPAddress.Parse(h!)).ToList();
        var best = pc.Select(a => (a, n: hosts.Count(h => SameSubnet(IPAddress.Parse(a.Ip), h, a.Mask))))
                     .Where(x => x.n > 0).OrderByDescending(x => x.n).Select(x => x.a).FirstOrDefault();
        return best ?? pc[0];
    }

    static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes(); var y = b.GetAddressBytes(); var m = mask.GetAddressBytes();
        for (var i = 0; i < 4; i++) if ((x[i] & m[i]) != (y[i] & m[i])) return false;
        return true;
    }
}
