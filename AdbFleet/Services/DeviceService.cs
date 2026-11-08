using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using AdbFleet.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AdbFleet.Services;

/// <summary>
/// Tracks every device the adb server knows about, plus devices seen before that aren't
/// connected now. There is no limit on how many devices can be connected; each one is
/// addressed by its own serial.
/// </summary>
public sealed partial class DeviceService : ObservableObject
{
    // Mid-luminance hues that stay readable on Mica in both light and dark themes.
    static readonly Windows.UI.Color[] Palette =
    [
        Rgb(0x2E, 0x9E, 0x8F), Rgb(0xD9, 0x82, 0x2B), Rgb(0x7C, 0x6C, 0xD9), Rgb(0xD1, 0x4D, 0x5B),
        Rgb(0x3B, 0x82, 0xC4), Rgb(0xA8, 0x93, 0x1E), Rgb(0xC2, 0x56, 0x9B), Rgb(0x4F, 0x9D, 0x4A),
        Rgb(0x2B, 0xA6, 0xC9), Rgb(0x9C, 0x6B, 0x3F),
    ];

    static Windows.UI.Color Rgb(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);
    static uint Pack(Windows.UI.Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
    static Windows.UI.Color Unpack(uint v) => Windows.UI.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    public ObservableCollection<Device> All { get; } = new();
    public ObservableCollection<Device> Checked { get; } = new();

    [ObservableProperty]
    public partial Device? Focused { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = "Starting adb";

    /// <summary>Raised whenever the set of devices an action would run on changes.</summary>
    public event Action? TargetsChanged;

    partial void OnFocusedChanged(Device? value) => TargetsChanged?.Invoke();

    /// <summary>Checked online devices, or the focused device when nothing is checked.</summary>
    public IReadOnlyList<Device> Targets
    {
        get
        {
            var c = Checked.Where(d => d.IsOnline).ToList();
            if (Checked.Count > 0) return c;
            return Focused is { IsOnline: true } f ? [f] : [];
        }
    }

    /// <summary>
    /// Second connections to a device that is already listed (a phone on USB and Wi-Fi at once),
    /// keyed by serial. They are tracked but not shown and not used for actions.
    /// </summary>
    readonly Dictionary<string, Device> _twins = new(StringComparer.Ordinal);

    // Placeholder serials some cheap devices share; never treat these as the same device.
    static readonly HashSet<string> JunkSerials = new(StringComparer.OrdinalIgnoreCase)
        { "", "unknown", "0123456789ABCDEF", "0123456789", "1234567890ABCDEF", "000000000000", "null" };

    static bool SamePhysical(KnownDevice a, KnownDevice b)
        => !JunkSerials.Contains(a.HardwareSerial) && a.HardwareSerial == b.HardwareSerial && a.Model == b.Model;

    /// <summary>Serials the user removed while still connected; ignored until they drop off.</summary>
    readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    CancellationTokenSource? _loop;
    bool _knownLoaded, _reconnectedOnce;

    List<KnownDevice> Known => Fleet.Settings.Value.KnownDevices;

    public void Start()
    {
        if (!_knownLoaded)
        {
            _knownLoaded = true;
            foreach (var k in Known)
            {
                // A device remembered over both USB and Wi-Fi shows once, as its Wi-Fi entry (that one can reconnect).
                if (!k.Serial.Contains(':') && Known.Any(o => o != k && o.Serial.Contains(':') && SamePhysical(k, o))) continue;
                var color = k.Color != 0 ? Unpack(k.Color) : NextColor();
                var d = new Device(k.Serial, color)
                {
                    State = Device.Absent,
                    Manufacturer = k.Manufacturer,
                    Model = k.Model,
                    AndroidVersion = k.AndroidVersion,
                    Address = k.Address,
                };
                d.PropertyChanged += OnDevicePropertyChanged;
                All.Add(d);
            }
            Focused ??= All.FirstOrDefault();
        }
        _loop?.Cancel();
        _loop = new CancellationTokenSource();
        var ct = _loop.Token;
        _ = Task.Run(() => Loop(ct));
    }

    async Task Loop(CancellationToken ct)
    {
        var started = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (Fleet.Adb.AdbPath == null)
                {
                    await Fleet.OnUI(() => Status = "adb.exe not found. Open Settings to set it up");
                    ToolLocator.Reset();
                }
                else
                {
                    if (!started)
                    {
                        await Fleet.OnUI(() => Status = "Starting adb server");
                        await Fleet.Adb.RunAsync(null, ["start-server"], ct, 30_000);
                        started = true;
                    }
                    var r = await Fleet.Adb.RunAsync(null, ["devices", "-l"], ct, 15_000);
                    if (r.ExitCode == 0)
                    {
                        var parsed = Parse(r.StdOut);
                        await Fleet.OnUI(() => Apply(parsed));
                        if (!_reconnectedOnce)
                        {
                            _reconnectedOnce = true;
                            _ = RefreshAvailability(reconnect: Fleet.Settings.Value.ReconnectOnStart, quiet: true);
                        }
                    }
                    else
                    {
                        started = false;
                        await Fleet.OnUI(() => Status = "adb server not responding: " + r.FirstLine);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { await Fleet.OnUI(() => Status = ex.Message); }
            try { await Task.Delay(1500, ct); } catch { return; }
        }
    }

    static List<(string serial, string state)> Parse(string output)
    {
        var list = new List<(string, string)>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices") || line.StartsWith('*')) continue;
            var parts = Regex.Split(line, @"\s+");
            if (parts.Length >= 2) list.Add((parts[0], parts[1]));
        }
        return list;
    }

    void Apply(List<(string serial, string state)> now)
    {
        _hidden.RemoveWhere(s => !now.Any(n => n.serial == s));
        now = now.Where(n => !_hidden.Contains(n.serial)).ToList();

        // Hidden second connections that dropped.
        foreach (var (serial, twin) in _twins.Where(t => !now.Any(n => n.serial == t.Key)).ToList())
        {
            _twins.Remove(serial);
            var owner = All.FirstOrDefault(d => d.Twin == twin);
            if (owner != null) owner.Twin = null;
            Fleet.Log.Add(owner ?? twin, $"{twin.TransportText} connection closed");
        }

        foreach (var gone in All.Where(d => !d.IsAbsent && !now.Any(n => n.serial == d.Serial)).ToList())
        {
            // The other connection to the same device is still up: let it take over this row.
            if (gone.Twin is { } twin && _twins.ContainsKey(twin.Serial))
            {
                Promote(gone, twin);
                continue;
            }
            Fleet.Log.Add(gone, $"Disconnected ({gone.TransportText})");
            if (Known.Any(k => k.Serial == gone.Serial))
            {
                // Keep remembered devices in the list, marked as not available.
                gone.State = Device.Absent;
                gone.IsChecked = false;
                gone.PropsLoaded = false;
                gone.Proxy = null;
            }
            else DropFromList(gone);
            TargetsChanged?.Invoke();
        }

        foreach (var (serial, state) in now)
        {
            if (_twins.TryGetValue(serial, out var tw))
            {
                tw.State = state;
                continue;
            }
            var d = All.FirstOrDefault(x => x.Serial == serial);
            if (d == null)
            {
                d = new Device(serial, NextColor()) { State = state };
                if (d.ConnectAddress != null) d.Address = d.ConnectAddress;
                d.PropertyChanged += OnDevicePropertyChanged;
                All.Add(d);
                Focused ??= d;
                if (d.PingHost != null) _ = CheckReach([d]);
            }
            else if (d.State != state)
            {
                d.State = state;
                TargetsChanged?.Invoke();
            }

            if (d.IsOnline && !d.PropsLoaded)
            {
                d.PropsLoaded = true;
                _ = LoadProps(d);
            }
        }

        var online = All.Count(d => d.IsOnline);
        var waiting = All.Count(d => !d.IsOnline && !d.IsAbsent);
        var absent = All.Count(d => d.IsAbsent);
        var parts = new List<string>();
        if (online > 0) parts.Add($"{online} ready");
        if (waiting > 0) parts.Add($"{waiting} waiting");
        if (absent > 0) parts.Add($"{absent} not connected");
        Status = parts.Count == 0 ? "No devices. Plug in a device or connect over Wi-Fi" : string.Join(", ", parts);
    }

    /// <summary>Replaces a dropped row with the same device's other connection, keeping colour, place and selection.</summary>
    void Promote(Device gone, Device twin)
    {
        _twins.Remove(twin.Serial);
        gone.Twin = null;
        twin.AdoptColor(gone.Color);
        twin.Info ??= gone.Info;
        twin.Proxy ??= gone.Proxy;
        if (twin.Reach == Reach.Unknown) twin.Reach = gone.Reach;
        var wasChecked = gone.IsChecked;
        var wasFocused = Focused == gone;
        var index = All.IndexOf(gone);
        gone.PropertyChanged -= OnDevicePropertyChanged;
        Checked.Remove(gone);
        twin.PropertyChanged += OnDevicePropertyChanged;
        All[index] = twin;
        if (wasFocused) Focused = twin;
        twin.IsChecked = wasChecked && twin.IsOnline;
        // Remember the device by the connection that can reconnect by itself (Wi-Fi), not by both.
        if (!gone.IsWifi) Known.RemoveAll(k => k.Serial == gone.Serial);
        Remember(twin);
        Fleet.Log.Add(twin, $"{gone.TransportText} connection closed; still connected over {twin.TransportText}");
        TargetsChanged?.Invoke();
    }

    /// <summary>
    /// After a device's properties load, checks whether it is a second connection to a device that is
    /// already listed (same hardware serial and model, and the same IP when both are known).
    /// </summary>
    void TryMerge(Device d)
    {
        if (JunkSerials.Contains(d.HardwareSerial)) return;
        var other = All.FirstOrDefault(o => o != d && o.HardwareSerial == d.HardwareSerial && o.Model == d.Model
                                            && o.IsWifi != d.IsWifi && SameIp(o, d));
        if (other == null)
        {
            // Remembered entries keep their hardware serial; match those too.
            var k = Known.FirstOrDefault(k => k.Serial != d.Serial && k.HardwareSerial == d.HardwareSerial && k.Model == d.Model);
            other = k == null ? null : All.FirstOrDefault(o => o.Serial == k.Serial && o.IsAbsent && o.IsWifi != d.IsWifi);
        }
        if (other == null) return;

        if (other.IsAbsent)
        {
            // The remembered row for this device comes alive through the new connection.
            var index = All.IndexOf(other);
            d.AdoptColor(other.Color);
            other.PropertyChanged -= OnDevicePropertyChanged;
            All.Remove(other);
            var at = All.IndexOf(d);
            if (at >= 0 && index >= 0 && index < All.Count && at != index) All.Move(at, index);
            if (Focused == other) Focused = d;
            Fleet.Log.Add(d, $"Same device as the remembered {other.TransportText} entry; showing it once");
        }
        else
        {
            // Both connections are live: keep the row that was there first, hide the new one.
            if (other.Twin != null) return;
            d.PropertyChanged -= OnDevicePropertyChanged;
            All.Remove(d);
            Checked.Remove(d);
            d.IsChecked = false;
            _twins[d.Serial] = d;
            other.Twin = d;
            if (Focused == d || Focused == null) Focused = other;
            Fleet.Log.Add(other, $"Also connected over {d.TransportText} ({d.Serial}); actions keep using {other.TransportText}");
        }
        TargetsChanged?.Invoke();
    }

    static bool SameIp(Device a, Device b)
        => a.PingHost == null || b.PingHost == null || a.PingHost == b.PingHost;

    void DropFromList(Device d)
    {
        d.PropertyChanged -= OnDevicePropertyChanged;
        All.Remove(d);
        Checked.Remove(d);
        if (Focused == d) Focused = All.FirstOrDefault(x => x.IsOnline) ?? All.FirstOrDefault();
    }

    Windows.UI.Color NextColor()
    {
        foreach (var c in Palette)
            if (!All.Any(d => d.Color == c)) return c;
        return Palette[All.Count % Palette.Length];
    }

    void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Device d || e.PropertyName != nameof(Device.IsChecked)) return;
        if (d.IsChecked && !Checked.Contains(d)) Checked.Add(d);
        else if (!d.IsChecked) Checked.Remove(d);
        TargetsChanged?.Invoke();
    }

    static readonly Regex PropLine = new(@"^\[(.+?)\]: \[(.*)\]$", RegexOptions.Compiled);

    async Task LoadProps(Device d)
    {
        try
        {
            var r = await Fleet.Adb.ShellAsync(d.Serial, "getprop", default, 20_000);
            var props = new Dictionary<string, string>();
            foreach (var line in r.StdOut.Split('\n'))
            {
                var m = PropLine.Match(line.Trim());
                if (m.Success) props[m.Groups[1].Value] = m.Groups[2].Value;
            }
            string P(params string[] keys) => keys.Select(k => props.GetValueOrDefault(k, "")).FirstOrDefault(v => v.Length > 0) ?? "";
            // Wi-Fi devices are addressed by ip:port already; USB and paired devices report their Wi-Fi IP.
            var ip = d.ConnectAddress ?? await DeviceOps.GetIp(d) ?? "";
            await Fleet.OnUI(() =>
            {
                d.Props = props;
                d.Manufacturer = P("ro.product.manufacturer", "ro.product.vendor.manufacturer", "ro.product.brand");
                d.Model = P("ro.product.model", "ro.product.vendor.model");
                d.AndroidVersion = P("ro.build.version.release", "ro.system.build.version.release", "ro.vendor.build.version.release");
                d.Sdk = int.TryParse(P("ro.build.version.sdk"), out var s) ? s : 0;
                d.Address = ip;
                d.HardwareSerial = P("ro.serialno", "ro.boot.serialno");
                Remember(d);
                TryMerge(d);
                // USB devices only learn their IP here; check it once.
                if (d.Reach == Reach.Unknown && d.PingHost != null) _ = CheckReach([d]);
                Fleet.Log.Add(d, $"Connected over {d.TransportText} (Android {d.AndroidVersion}, {(ip.Length > 0 ? ip : d.Serial)})", LogKind.Success);
                TargetsChanged?.Invoke();
            });
            await Task.WhenAll(ProxyOps.Refresh(d), InfoOps.Load(d));
            if (d.HasProxy) Fleet.Log.Add(d, $"Uses a proxy: {d.Proxy}");
        }
        catch (Exception ex)
        {
            d.PropsLoaded = false;
            Fleet.Log.Error(d, "Could not read device details: " + ex.Message);
        }
    }

    void Remember(Device d)
    {
        var k = Known.FirstOrDefault(x => x.Serial == d.Serial);
        if (k == null) { k = new KnownDevice { Serial = d.Serial }; Known.Add(k); }
        k.Manufacturer = d.Manufacturer;
        k.Model = d.Model;
        k.AndroidVersion = d.AndroidVersion;
        k.Address = d.Address;
        k.HardwareSerial = d.HardwareSerial;
        k.Color = Pack(d.Color);
        Fleet.Settings.Save();
    }

    /// <summary>Disconnects (for Wi-Fi) and forgets the device so it no longer shows in the list.</summary>
    public async Task Remove(Device d)
    {
        if (d.Twin is { } twin)
        {
            _twins.Remove(twin.Serial);
            d.Twin = null;
            if (twin.IsWifi) await Fleet.Exec(null, "disconnect", twin.Serial);
            else _hidden.Add(twin.Serial);
            Known.RemoveAll(k => k.Serial == twin.Serial);
        }
        if (d.IsWifi && !d.IsAbsent)
            await Fleet.Exec(null, "disconnect", d.Serial);
        else if (!d.IsWifi && !d.IsAbsent)
            _hidden.Add(d.Serial); // a plugged-in USB device stays hidden until it is unplugged
        Known.RemoveAll(k => k.Serial == d.Serial);
        Fleet.Settings.Save();
        DropFromList(d);
        Fleet.Log.Add(null, $"Removed {d.DisplayName} from the device list");
        TargetsChanged?.Invoke();
    }

    public async Task<string?> Reconnect(Device d)
    {
        if (d.ConnectAddress is not { } host)
            return d.IsWifi ? "this device was paired through discovery; reconnect it from Connect over Wi-Fi" : "USB devices reconnect when the cable is plugged in";
        return await DeviceOps.Connect(host);
    }

    bool _refreshing;

    /// <summary>
    /// Pings every device, then (optionally) reconnects remembered Wi-Fi devices that answered
    /// but aren't connected. Unreachable devices are not retried, so this never waits on dead addresses.
    /// </summary>
    public async Task RefreshAvailability(bool reconnect, bool quiet)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var devices = await Fleet.OnUIGet(() => All.ToList());
            await Task.WhenAll(CheckReach(devices), ProxyOps.RefreshAll(devices), InfoOps.LoadAll(devices));
            if (!reconnect) return;

            var hosts = await Fleet.OnUIGet(() => All
                .Where(d => d.IsAbsent && d.Reach == Reach.Reachable && d.ConnectAddress != null)
                .Select(d => d.ConnectAddress!).ToList());
            if (!quiet)
            {
                var up = devices.Count(d => d.Reach == Reach.Reachable);
                var down = devices.Count(d => d.Reach == Reach.Unreachable);
                Fleet.Log.Add(null, $"Availability: {up} reachable, {down} not reachable" +
                    (hosts.Count > 0 ? $"; reconnecting {hosts.Count}" : ""));
            }
            await Task.WhenAll(hosts.Select(async h =>
            {
                var r = await Fleet.Adb.RunAsync(null, ["connect", h], default, 10_000);
                var ok = r.Text.Contains("connected to", StringComparison.Ordinal) && !r.Text.Contains("failed", StringComparison.OrdinalIgnoreCase);
                Fleet.Log.Add(null, $"connect {h}: {r.FirstLine}", ok ? LogKind.Output : LogKind.Error);
            }));
        }
        finally { _refreshing = false; }
    }

    /// <summary>
    /// Checks whether each device answers on the network: ICMP ping first, then a TCP connect to
    /// its adb port for devices that block ping. This is independent of the adb connection.
    /// </summary>
    public static Task CheckReach(IReadOnlyList<Device> devices) => Task.WhenAll(devices.Select(async d =>
    {
        var (host, port) = await Fleet.OnUIGet(() => (d.PingHost, d.AdbPort));
        if (host == null)
        {
            await Fleet.OnUI(() =>
            {
                d.Reach = Reach.Unknown;
                d.ReachText = d.IsWifi ? "No IP address known for this device" : "No Wi-Fi address to check. Connected by cable only";
            });
            return;
        }
        await Fleet.OnUI(() => { d.Reach = Reach.Checking; d.ReachText = $"Checking {host}"; });

        Reach result;
        string text;
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(host, 1200);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                result = Reach.Reachable;
                text = $"{host} replies to ping in {reply.RoundtripTime} ms";
            }
            else if (await TcpOpen(host, port))
            {
                result = Reach.Reachable;
                text = $"{host} blocks ping, but its adb port {port} is open";
            }
            else
            {
                result = Reach.Unreachable;
                text = $"{host} doesn't reply. It may be off, asleep, or on another network";
            }
        }
        catch (Exception ex)
        {
            result = Reach.Unreachable;
            text = $"Couldn't check {host}: {ex.Message}";
        }
        var checkedAt = DateTime.Now.ToString("HH:mm");
        await Fleet.OnUI(() => { d.Reach = result; d.ReachText = $"{text} (checked {checkedAt})"; });
    }));

    static async Task<bool> TcpOpen(string host, int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(1200);
            await c.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    public void CheckAll(bool value)
    {
        foreach (var d in All) d.IsChecked = value && d.IsOnline;
    }
}
