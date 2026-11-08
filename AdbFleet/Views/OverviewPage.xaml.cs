using System.ComponentModel;
using System.Globalization;
using System.Text;
using AdbFleet.Models;
using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace AdbFleet.Views;

/// <summary>
/// The first page: everything about the selected device, or a comparison table when two or more
/// devices are checked. Details are read in the background when a device connects, so this page
/// only displays them; Refresh reads them again.
/// </summary>
public sealed partial class OverviewPage : Page
{
    Device? _single;
    string _sortKey = "";
    bool _sortDesc;
    readonly Dictionary<string, string> _headerText = new();

    public OverviewPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Fleet.Devices.TargetsChanged += OnTargets;
        Fleet.Devices.PropertyChanged += OnDevices;
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Fleet.Devices.TargetsChanged -= OnTargets;
        Fleet.Devices.PropertyChanged -= OnDevices;
        Watch(null);
    }

    void OnTargets() => DispatcherQueue.TryEnqueue(Render);

    void OnDevices(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceService.Focused)) DispatcherQueue.TryEnqueue(Render);
    }

    /// <summary>Two or more checked devices: compare them. Otherwise: the one checked or selected device.</summary>
    void Render()
    {
        var svc = Fleet.Devices;
        var checkedList = svc.Checked.Where(d => svc.All.Contains(d)).ToList();
        if (checkedList.Count >= 2)
        {
            Watch(null);
            ShowOnly(TableView);
            TableTitle.Text = $"Comparing {checkedList.Count} devices";
            Table.ItemsSource = Sorted(checkedList);
            LoadMissing(checkedList);
            return;
        }

        var d = checkedList.Count == 1 ? checkedList[0] : svc.Focused;
        if (d == null)
        {
            Watch(null);
            ShowOnly(EmptyView);
            return;
        }
        ShowOnly(SingleView);
        Watch(d);
        RenderSingle();
        LoadMissing([d]);
    }

    void ShowOnly(UIElement view)
    {
        foreach (var v in new UIElement[] { EmptyView, SingleView, TableView })
            v.Visibility = v == view ? Visibility.Visible : Visibility.Collapsed;
    }

    static void LoadMissing(IEnumerable<Device> devices)
    {
        foreach (var d in devices.Where(d => d.IsOnline && d.Info == null && !d.InfoLoading && d.PropsLoaded))
            _ = InfoOps.Load(d);
    }

    void Watch(Device? d)
    {
        if (_single == d) return;
        if (_single != null) _single.PropertyChanged -= OnSingleChanged;
        _single = d;
        if (d != null) d.PropertyChanged += OnSingleChanged;
    }

    void OnSingleChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Device.IsChecked) or nameof(Device.Run) or nameof(Device.RunMessage)
            or nameof(Device.IsBusy) or nameof(Device.ResultGlyph) or nameof(Device.ResultBrush) or nameof(Device.ResultVisibility)) return;
        DispatcherQueue.TryEnqueue(RenderSingle);
    }

    // ---------------- One device ----------------

    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    void RenderSingle()
    {
        if (_single is not { } d) return;
        var i = d.Info;

        HeadBar.Fill = d.Brush;
        HeadName.Text = d.DisplayName;
        HeadAndroid.Text = d.AndroidVersion.Length > 0 ? $"Android {d.AndroidVersion}" + (d.Sdk > 0 ? $" (API {d.Sdk})" : "") : "Android version not read yet";
        HeadTransportIcon.Glyph = d.TransportGlyph;
        HeadConnection.Text = d.ConnectionText + (!d.IsWifi && d.Address.Length > 0 ? $", Wi-Fi IP {d.Address}" : "");
        HeadReachDot.Fill = d.ReachBrush;
        HeadReach.Text = d.Reach switch
        {
            Reach.Reachable => "Reachable",
            Reach.Unreachable => "Not reachable",
            Reach.Checking => "Checking",
            _ => d.PingHost == null ? "No IP to check" : "Not checked",
        };
        ToolTipService.SetToolTip(HeadReach, d.ReachText);
        HeadProxy.Visibility = d.HasProxy ? Visibility.Visible : Visibility.Collapsed;
        HeadProxyText.Text = $"Proxy {d.Proxy}";
        ReadAt.Text = d.InfoLoading ? "Reading details" : i?.ReadAtText ?? "";
        RefreshSingle.IsEnabled = d.IsOnline && !d.InfoLoading;

        if (!d.IsOnline)
        {
            OfflineBar.Title = "Not connected";
            OfflineBar.Message = d.Subtitle + ". " + (i != null
                ? $"These details were read at {i.ReadAt:HH:mm} and may be out of date."
                : "Connect it to read its storage, memory and other details.");
            OfflineBar.IsOpen = true;
        }
        else OfflineBar.IsOpen = false;

        Metrics.Visibility = i != null ? Visibility.Visible : Visibility.Collapsed;
        if (i != null)
        {
            Metric(StorageValue, StorageBar, StorageNote, i.StorageTotalKb > 0,
                $"{Gb(i.StorageFreeKb)} free", i.StorageUsedPercent, $"of {Gb(i.StorageTotalKb)}, {i.StorageUsedPercent:0}% used", i.StorageLow);
            Metric(MemoryValue, MemoryBar, MemoryNote, i.MemTotalKb > 0,
                $"{Gb(i.MemAvailKb)} free", i.MemoryUsedPercent, $"of {Gb(i.MemTotalKb)}, {i.MemoryUsedPercent:0}% in use", i.MemoryLow);
            if (i.Battery >= 0)
            {
                ThirdLabel.Text = "Battery";
                Metric(ThirdValue, ThirdBar, ThirdNote, true, $"{i.Battery}%", i.Battery,
                    i.Charging ? "Charging" : "On battery", i.BatteryLow);
            }
            else
            {
                // TVs and boxes have no battery; up time is the more useful number there.
                ThirdLabel.Text = "Up time";
                Metric(ThirdValue, ThirdBar, ThirdNote, i.Uptime > TimeSpan.Zero, i.UptimeText, -1,
                    i.Uptime > TimeSpan.Zero ? $"Since {DateTime.Now - i.Uptime:d MMM, HH:mm}" : "", false);
            }
        }

        SystemList.ItemsSource = SystemFacts(d);
        DisplayList.ItemsSource = DisplayFacts(d);
        NetworkList.ItemsSource = NetworkFacts(d);
    }

    static string Gb(long kb) => kb >= 1048576 ? $"{kb / 1048576.0:0.0} GB" : $"{kb / 1024.0:0} MB";

    static void Metric(TextBlock value, ProgressBar bar, TextBlock note, bool known, string text, double percent, string noteText, bool warn)
    {
        value.Text = known ? text : "Unknown";
        value.Foreground = Res(warn ? "SystemFillColorCautionBrush" : "TextFillColorPrimaryBrush");
        bar.Visibility = known && percent >= 0 ? Visibility.Visible : Visibility.Collapsed;
        bar.Value = Math.Max(0, percent);
        bar.Foreground = warn ? Res("SystemFillColorCautionBrush") : Res("AccentFillColorDefaultBrush");
        note.Text = known ? noteText : "";
    }

    static List<Fact> SystemFacts(Device d)
    {
        var i = d.Info;
        string P(string k) => d.Props.GetValueOrDefault(k, "");
        var list = new List<Fact>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) list.Add(new Fact(label, value)); }
        Add("Manufacturer", d.Manufacturer);
        Add("Model", d.Model);
        Add("Device code", i?.Codename ?? P("ro.product.device"));
        Add("Android", d.AndroidVersion.Length > 0 ? $"{d.AndroidVersion}" + (d.Sdk > 0 ? $" (API {d.Sdk})" : "") : null);
        Add("Security patch", i?.PatchText ?? P("ro.build.version.security_patch"));
        Add("Build", i?.Build ?? P("ro.build.display.id"));
        Add("CPU", i?.Cpu ?? P("ro.product.cpu.abi"));
        Add("Serial number", d.HardwareSerial);
        Add("adb serial", d.Serial + (d.Twin != null ? $"  and  {d.Twin.Serial}" : ""));
        return list;
    }

    static List<Fact> DisplayFacts(Device d)
    {
        var i = d.Info;
        return
        [
            new("Resolution", i?.ScreenText ?? "Not read yet"),
            new("Density", i?.DensityText ?? "Not read yet"),
        ];
    }

    static List<Fact> NetworkFacts(Device d)
    {
        var list = new List<Fact>
        {
            new("Connection", d.Twin != null ? $"USB and Wi-Fi (actions use {d.TransportText})" : d.TransportText),
        };
        var wifi = d.IsWifi ? d.ConnectAddress ?? d.Serial : d.Twin?.IsWifi == true ? d.Twin.Serial : null;
        if (wifi != null) list.Add(new("adb address", wifi));
        list.Add(new("IP address", d.PingHost ?? "No Wi-Fi address"));
        list.Add(new("Availability", d.ReachText));
        list.Add(new("Proxy", d.ProxyStatus));
        if (d.Info is { Uptime.TotalSeconds: > 0 } i) list.Add(new("Up time", i.UptimeText));
        return list;
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_single is not { } d) return;
        var sb = new StringBuilder();
        sb.AppendLine(d.DisplayName);
        void Section(string title, IEnumerable<Fact> facts)
        {
            sb.AppendLine().AppendLine(title);
            foreach (var f in facts) sb.AppendLine($"  {f.Label}: {f.Value}");
        }
        if (d.Info is { } i)
            Section("Usage", [new("Storage", i.StorageText), new("Memory", i.MemoryText), new("Battery", i.BatteryText)]);
        Section("System", SystemFacts(d));
        Section("Display", DisplayFacts(d));
        Section("Network", NetworkFacts(d));
        SetClipboard(sb.ToString());
        Fleet.Log.Add(d, "Copied device details to the clipboard");
    }

    static void SetClipboard(string text)
    {
        var p = new DataPackage();
        p.SetText(text);
        Clipboard.SetContent(p);
    }

    async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var svc = Fleet.Devices;
        var checkedList = svc.Checked.Where(d => svc.All.Contains(d)).ToList();
        var devices = (checkedList.Count >= 1 ? checkedList : _single != null ? [_single] : new List<Device>())
            .Where(d => d.IsOnline).ToList();
        if (devices.Count == 0) return;
        RefreshSingle.IsEnabled = RefreshTable.IsEnabled = false;
        try
        {
            var reach = DeviceService.CheckReach(devices);
            await Batch.Run("Refresh details", devices, async d =>
            {
                var err = await InfoOps.Load(d);
                await ProxyOps.Refresh(d);
                return err;
            });
            await reach;
        }
        finally
        {
            RefreshSingle.IsEnabled = RefreshTable.IsEnabled = true;
            Render();
        }
    }

    // ---------------- Comparison table ----------------

    List<Device> Sorted(List<Device> list)
    {
        static double Num(DeviceInfo? i, Func<DeviceInfo, double> f) => i == null ? double.MinValue : f(i);
        Func<Device, IComparable> key = _sortKey switch
        {
            "android" => d => d.Sdk,
            "patch" => d => d.Info?.SecurityPatch ?? "",
            "storage" => d => Num(d.Info, i => i.StorageFreeKb),
            "memory" => d => Num(d.Info, i => i.MemAvailKb),
            "battery" => d => Num(d.Info, i => i.Battery),
            "screen" => d => Num(d.Info, i => ScreenPixels(i)),
            "uptime" => d => Num(d.Info, i => i.Uptime.TotalSeconds),
            "proxy" => d => d.Proxy ?? "",
            "name" => d => d.DisplayName,
            _ => d => Fleet.Devices.All.IndexOf(d),
        };
        return (_sortDesc ? list.OrderByDescending(key) : list.OrderBy(key)).ToList();
    }

    static double ScreenPixels(DeviceInfo i)
    {
        var s = (i.ScreenOverride.Length > 0 ? i.ScreenOverride : i.Screen).Split('x');
        return s.Length == 2 && double.TryParse(s[0], out var w) && double.TryParse(s[1], out var h) ? w * h : 0;
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string key) return;
        _sortDesc = _sortKey == key && !_sortDesc;
        _sortKey = key;
        // Mark the sorted column with an arrow.
        if (b.Parent is Panel header)
        {
            foreach (var hb in header.Children.OfType<Button>())
            {
                var tag = (string)hb.Tag;
                if (!_headerText.ContainsKey(tag)) _headerText[tag] = (string)hb.Content;
                hb.Content = tag == key ? $"{_headerText[tag]} {(_sortDesc ? "↓" : "↑")}" : _headerText[tag];
            }
        }
        Render();
    }

    void Table_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Device d) Fleet.Devices.Focused = d;
    }

    void CopyCsv_Click(object sender, RoutedEventArgs e)
    {
        if (Table.ItemsSource is not List<Device> rows) return;
        static string Q(string v) => v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
        static string G(long kb) => kb > 0 ? (kb / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) : "";
        var sb = new StringBuilder();
        sb.AppendLine("Device,Connection,Android,API,Security patch,Storage free GB,Storage total GB,Memory free GB,Memory total GB,Battery %,Screen,Density,Up time hours,Proxy,Serial number");
        foreach (var d in rows)
        {
            var i = d.Info;
            sb.AppendLine(string.Join(",", new[]
            {
                d.DisplayName, d.ConnectionText, d.AndroidVersion, d.Sdk > 0 ? d.Sdk.ToString() : "",
                i?.SecurityPatch ?? "", G(i?.StorageFreeKb ?? 0), G(i?.StorageTotalKb ?? 0),
                G(i?.MemAvailKb ?? 0), G(i?.MemTotalKb ?? 0), i is { Battery: >= 0 } ? i.Battery.ToString() : "",
                i == null ? "" : i.ScreenOverride.Length > 0 ? i.ScreenOverride : i.Screen,
                i == null ? "" : i.DensityOverride.Length > 0 ? i.DensityOverride : i.Density,
                i is { Uptime.TotalSeconds: > 0 } ? i.Uptime.TotalHours.ToString("0.0", CultureInfo.InvariantCulture) : "",
                d.Proxy ?? "", d.HardwareSerial,
            }.Select(Q)));
        }
        SetClipboard(sb.ToString());
        Fleet.Log.Add(null, $"Copied details of {rows.Count} devices as CSV");
    }
}
