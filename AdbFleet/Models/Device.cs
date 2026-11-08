using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AdbFleet.Models;

public enum RunState { Idle, Busy, Ok, Failed }

public enum Reach { Unknown, Checking, Reachable, Unreachable }

/// <summary>
/// One adb transport (USB or network), or a remembered device that isn't connected right now
/// (State == "absent"). Always created and mutated on the UI thread.
/// </summary>
public partial class Device : ObservableObject
{
    public const string Absent = "absent";

    public Device(string serial, Windows.UI.Color color)
    {
        Serial = serial;
        Color = color;
        Brush = new SolidColorBrush(color);
    }

    public string Serial { get; }
    public Windows.UI.Color Color { get; private set; }
    public SolidColorBrush Brush { get; }

    /// <summary>Takes over another row's colour (used when a USB and Wi-Fi row merge), updating every brush in place.</summary>
    public void AdoptColor(Windows.UI.Color c)
    {
        Color = c;
        Brush.Color = c;
    }

    /// <summary>ro.serialno: the same for the USB and Wi-Fi connection of one physical device.</summary>
    public string HardwareSerial { get; set; } = "";
    public Dictionary<string, string> Props { get; set; } = new();
    public int Sdk { get; set; }
    public bool PropsLoaded { get; set; }

    public bool IsWifi => Serial.Contains(':') || Serial.Contains("._adb-tls-connect", StringComparison.Ordinal);
    public string TransportGlyph => IsWifi ? "" : "";
    public string TransportText => IsWifi ? "Wi-Fi" : "USB";

    /// <summary>Host:port for network devices, which is what "adb connect" needs.</summary>
    public string? ConnectAddress => Serial.Contains(':') && !Serial.Contains("._adb", StringComparison.Ordinal) ? Serial : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(IsOnline), nameof(IsAbsent), nameof(RowOpacity), nameof(ProxyStatus))]
    public partial string State { get; set; } = "";

    /// <summary>
    /// The same physical device's other connection (USB while this row is Wi-Fi, or the reverse).
    /// It is kept out of the list and out of actions; it takes over this row if this connection drops.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(SummaryTip), nameof(ConnectionText))]
    public partial Device? Twin { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryTip), nameof(HasInfo), nameof(InfoVisibility), nameof(PatchBrush), nameof(StorageBrush), nameof(MemoryBrush), nameof(BatteryBrush))]
    public partial DeviceInfo? Info { get; set; }

    [ObservableProperty]
    public partial bool InfoLoading { get; set; }

    // Amber when a value needs a look; normal text otherwise (also while details aren't read yet).
    static Brush Warn(bool w) => (Brush)Application.Current.Resources[w ? "SystemFillColorCautionBrush" : "TextFillColorPrimaryBrush"];
    public Brush PatchBrush => Warn(Info?.PatchOld == true);
    public Brush StorageBrush => Warn(Info?.StorageLow == true);
    public Brush MemoryBrush => Warn(Info?.MemoryLow == true);
    public Brush BatteryBrush => Warn(Info?.BatteryLow == true);

    public bool HasInfo => Info != null;
    public Visibility InfoVisibility => Info != null ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Manufacturer { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string Model { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string AndroidVersion { get; set; } = "";

    /// <summary>IP address (and port for Wi-Fi). For USB devices this is their Wi-Fi IP, if they have one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AddressText), nameof(PingHost))]
    public partial string Address { get; set; } = "";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(ResultGlyph), nameof(ResultVisibility), nameof(ResultBrush))]
    public partial RunState Run { get; set; }

    [ObservableProperty]
    public partial string RunMessage { get; set; } = "";

    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model)) return Serial;
            var m = Manufacturer.Trim();
            // Avoid "Google Google Pixel" style repetition.
            if (m.Length > 0 && Model.StartsWith(m, StringComparison.OrdinalIgnoreCase)) return Model;
            return $"{(m.Length > 0 ? char.ToUpper(m[0]) + m[1..] : "")} {Model}".Trim();
        }
    }

    public string AddressText => Address.Length > 0 ? Address : IsWifi ? Serial : "No Wi-Fi address";

    public string Subtitle => State switch
    {
        "device" => (AndroidVersion.Length > 0 ? $"Android {AndroidVersion}" : "Reading device details")
                    + (Twin != null ? $", also on {(Twin.IsWifi ? "Wi-Fi" : "USB")}" : ""),
        Absent when !IsWifi => "Not connected. Plug in the cable",
        Absent when Reach == Reach.Unreachable => "Not connected. Not reachable on the network",
        Absent => "Not connected. Double-click to reconnect",
        "unauthorized" => "Accept the debugging prompt on the device",
        "authorizing" => "Waiting for authorization",
        "offline" => "Offline. Reconnect the cable or network",
        "recovery" => "In recovery mode",
        "sideload" => "In sideload mode",
        "bootloader" => "In bootloader",
        _ => State,
    };

    /// <summary>How the device is connected, e.g. "Wi-Fi 192.168.1.20:5555" or "USB and Wi-Fi".</summary>
    public string ConnectionText => Twin != null ? "USB and Wi-Fi"
        : IsWifi ? $"Wi-Fi {ConnectAddress ?? Serial}" : "USB";

    /// <summary>Tooltip for the device name in the list.</summary>
    public string SummaryTip
    {
        get
        {
            var lines = new List<string> { DisplayName };
            if (AndroidVersion.Length > 0) lines.Add($"Android {AndroidVersion}" + (Sdk > 0 ? $" (API {Sdk})" : ""));
            lines.Add(ConnectionText + (Address.Length > 0 && !IsWifi ? $", Wi-Fi IP {Address}" : ""));
            if (Info is { } i)
            {
                lines.Add($"Storage: {i.StorageText}");
                lines.Add($"Memory: {i.MemoryText}");
                if (i.Battery >= 0) lines.Add($"Battery: {i.BatteryText}");
                if (i.SecurityPatch.Length > 0) lines.Add($"Security patch: {i.PatchText}");
            }
            if (HasProxy) lines.Add($"Proxy: {Proxy}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public bool IsOnline => State == "device";
    public bool IsAbsent => State == Absent;
    public double RowOpacity => IsOnline ? 1.0 : 0.7;

    // ---- Network reachability (independent of the adb connection) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReachBrush), nameof(IsChecking), nameof(Subtitle))]
    public partial Reach Reach { get; set; }

    [ObservableProperty]
    public partial string ReachText { get; set; } = "Not checked yet. Use the refresh button below the list";

    public bool IsChecking => Reach == Reach.Checking;

    /// <summary>IPv4 address to ping, taken from the address without its port.</summary>
    public string? PingHost
    {
        get
        {
            var a = Address.Length > 0 ? Address : ConnectAddress ?? "";
            var host = a.Contains(':') ? a[..a.LastIndexOf(':')] : a;
            return System.Net.IPAddress.TryParse(host, out _) ? host : null;
        }
    }

    public int AdbPort
    {
        get
        {
            var a = ConnectAddress ?? "";
            return a.Contains(':') && int.TryParse(a[(a.LastIndexOf(':') + 1)..], out var p) ? p : 5555;
        }
    }

    /// <summary>Green: answers on the network. Red: no answer. Grey: not checked or no address.</summary>
    public Brush ReachBrush => (Brush)Application.Current.Resources[Reach switch
    {
        Reach.Reachable => "SystemFillColorSuccessBrush",
        Reach.Unreachable => "SystemFillColorCriticalBrush",
        _ => "ControlStrongFillColorDisabledBrush",
    }];

    // ---- Global HTTP proxy (settings global http_proxy) ----

    /// <summary>null: not checked yet. Empty: no proxy. Otherwise host:port.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProxy), nameof(ProxyVisibility), nameof(ProxyStatus), nameof(ProxyBadge), nameof(SummaryTip), nameof(ProxyBrush))]
    public partial string? Proxy { get; set; }

    public bool HasProxy => !string.IsNullOrEmpty(Proxy);
    public Visibility ProxyVisibility => HasProxy ? Visibility.Visible : Visibility.Collapsed;
    public string ProxyBadge => Proxy ?? "";
    public Brush ProxyBrush => (Brush)Application.Current.Resources[HasProxy ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"];
    public string ProxyStatus => Proxy switch
    {
        null => IsOnline ? "Not checked yet" : "Not connected",
        "" => "No proxy",
        _ => Proxy,
    };

    public bool IsBusy => Run == RunState.Busy;
    public Visibility ResultVisibility => Run is RunState.Ok or RunState.Failed ? Visibility.Visible : Visibility.Collapsed;
    public string ResultGlyph => Run switch { RunState.Ok => "", RunState.Failed => "", _ => "" };

    public Brush ResultBrush => (Brush)Application.Current.Resources[
        Run == RunState.Failed ? "SystemFillColorCriticalBrush" : "SystemFillColorSuccessBrush"];

    /// <summary>File-system friendly name used for screenshots and saved APK folders.</summary>
    public string SafeName
    {
        get
        {
            var s = DisplayName;
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}

/// <summary>What is remembered about a device between sessions.</summary>
public sealed class KnownDevice
{
    public string Serial { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string AndroidVersion { get; set; } = "";
    public string Address { get; set; } = "";
    public uint Color { get; set; }
    public string HardwareSerial { get; set; } = "";
}
