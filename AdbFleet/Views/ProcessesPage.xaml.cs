using System.ComponentModel;
using AdbFleet.Models;
using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AdbFleet.Views;

public sealed partial class ProcessesPage : Page
{
    List<ProcessRow> _rows = new();
    Device? _device;
    bool _loading;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public ProcessesPage()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => _ = Load(fromTimer: true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Fleet.Devices.PropertyChanged += OnDevices;
        Fleet.Devices.TargetsChanged += OnTargets;
        if (AutoRefresh.IsOn) _timer.Start();
        _ = Load();
    }

    void OnTargets() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_device != null) Title.Text = $"Running processes on {_device.DisplayName}";
        // The shown device just came online (e.g. reconnected): load it now.
        if (_device is { IsOnline: true } && _rows.Count == 0 && !_loading) _ = Load();
    });

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Fleet.Devices.PropertyChanged -= OnDevices;
        Fleet.Devices.TargetsChanged -= OnTargets;
        _timer.Stop();
    }

    void OnDevices(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceService.Focused)) _ = Load();
    }

    int _loadId;

    async Task Load(bool fromTimer = false)
    {
        // Timer ticks never pile up; a device switch always starts a fresh load.
        if (fromTimer && _loading) return;
        var id = ++_loadId;
        var d = Fleet.Devices.Focused;
        if (_device != d)
        {
            // Never show the previous device's processes under the new device's name.
            _rows = new();
            Apply();
        }
        _device = d;
        SourceDot.Fill = d?.Brush;
        Title.Text = d == null ? "Running processes" : $"Running processes on {d.DisplayName}";
        if (d is not { IsOnline: true }) { _rows = new(); Apply(); return; }
        _loading = true;
        LoadingRing.IsActive = _rows.Count == 0;
        try
        {
            var rows = await Task.Run(() => DeviceOps.Processes(d));
            if (id == _loadId && _device == d) { _rows = rows; Apply(); }
        }
        catch (Exception ex) { Fleet.Log.Error(d, "Could not list processes: " + ex.Message); }
        finally
        {
            if (id == _loadId) { _loading = false; LoadingRing.IsActive = false; }
        }
    }

    void Apply()
    {
        var keep = List.SelectedItems.Cast<ProcessRow>().Select(r => r.Name).ToHashSet();
        var q = Search.Text.Trim();
        var shown = _rows.Where(r => AppsOnly.IsChecked != true || r.IsApp)
            .Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        List.ItemsSource = shown;
        foreach (var r in shown) if (keep.Contains(r.Name)) List.SelectedItems.Add(r);
        var mb = shown.Sum(r => r.RssKb) / 1024.0;
        Summary.Text = _rows.Count == 0 ? "" : $"{shown.Count} processes using {mb:0} MB";
    }

    void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Apply();
    void AppsOnly_Click(object sender, RoutedEventArgs e) => Apply();
    void Refresh_Click(object sender, RoutedEventArgs e) => _ = Load();

    void AutoRefresh_Toggled(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsOn) _timer.Start(); else _timer.Stop();
    }

    async void Stop_Click(object sender, RoutedEventArgs e)
    {
        var d = _device;
        // Process names of app components look like "com.app:service"; force-stop takes the package.
        var pkgs = List.SelectedItems.Cast<ProcessRow>().Select(r => r.Name.Split(':')[0]).Where(n => n.Contains('.')).Distinct().ToList();
        if (d == null || pkgs.Count == 0)
        {
            Fleet.Log.Add(null, "Select one or more app processes to stop. System processes can't be stopped without root.");
            return;
        }
        await Batch.RunItems("Force stop", [d], pkgs, PackageOps.ForceStop);
        await Load();
    }
}
