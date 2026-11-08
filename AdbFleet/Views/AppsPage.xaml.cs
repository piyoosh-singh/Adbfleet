using System.ComponentModel;
using AdbFleet.Models;
using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AdbFleet.Views;

public sealed partial class AppsPage : Page
{
    List<AppInfo> _all = new();
    readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    Device? _source;
    bool _suppress;
    int _loadId;
    string _filter = "user";
    DispatcherTimerLite? _searchDebounce;

    public AppsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Fleet.Devices.PropertyChanged += OnDevicesChanged;
        Fleet.Devices.TargetsChanged += OnTargetsChanged;
        if (_source != Fleet.Devices.Focused || _all.Count == 0) _ = Load();
        UpdateHeader();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Fleet.Devices.PropertyChanged -= OnDevicesChanged;
        Fleet.Devices.TargetsChanged -= OnTargetsChanged;
    }

    // Device names arrive a moment after the device appears; targets also change when boxes are checked.
    void OnTargetsChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_source != null) SourceText.Text = $"Apps on {_source.DisplayName}";
        UpdateHeader();
        // The shown device just came online (e.g. reconnected): load it now.
        if (_source is { IsOnline: true } && _all.Count == 0 && !LoadingRing.IsActive) _ = Load();
    });

    void OnDevicesChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DeviceService.Focused) && Fleet.Devices.Focused != _source) _ = Load();
    }

    void UpdateHeader()
    {
        var count = _selected.Count;
        SelectedText.Text = count == 0 ? "" : count == 1 ? "1 app selected" : $"{count} apps selected";
        Actions.IsEnabled = count > 0 && Fleet.Devices.Targets.Count > 0;
    }

    async Task Load()
    {
        var id = ++_loadId;
        var d = Fleet.Devices.Focused;
        if (_source != d)
        {
            // Never show the previous device's apps under the new device's name.
            _all = new();
            ApplyFilter();
        }
        _source = d;
        SourceDot.Fill = d?.Brush;
        SourceText.Text = d == null ? "Apps" : $"Apps on {d.DisplayName}";
        if (d == null || !d.IsOnline)
        {
            _all = new();
            ShowEmpty(d == null ? "No device selected" : "This device isn't ready",
                d == null ? "Select a device on the left to see its apps." : d.Subtitle);
            ApplyFilter();
            return;
        }

        LoadingRing.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            var list = await Task.Run(() => PackageOps.ListAsync(d));
            if (id != _loadId) return;
            _all = list;
            // Keep selections only for packages that exist on the newly shown device.
            _selected.IntersectWith(list.Select(a => a.Package));
            ApplyFilter();
        }
        catch (Exception ex)
        {
            if (id != _loadId) return;
            _all = new();
            ApplyFilter();
            ShowEmpty("Couldn't read the app list", ex.Message);
        }
        finally
        {
            if (id == _loadId) LoadingRing.IsActive = false;
        }
    }

    void ShowEmpty(string title, string body)
    {
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyState.Visibility = Visibility.Visible;
    }

    bool Matches(AppInfo a) => _filter switch
    {
        "user" => !a.IsSystem && a.IsInstalled,
        "system" => a.IsSystem && a.IsInstalled,
        "disabled" => !a.IsEnabled && a.IsInstalled,
        "removed" => !a.IsInstalled,
        _ => true,
    };

    void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var shown = _all.Where(Matches)
            .Where(a => q.Length == 0 || a.Package.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _suppress = true;
        AppList.ItemsSource = shown;
        foreach (var a in shown)
            if (_selected.Contains(a.Package)) AppList.SelectedItems.Add(a);
        _suppress = false;

        if (_all.Count > 0)
        {
            if (shown.Count == 0)
                ShowEmpty(q.Length > 0 ? $"No apps match \"{q}\"" : "Nothing here",
                    q.Length > 0 ? "Try a shorter search, or look under All." : _filter switch
                    {
                        "disabled" => "No apps are disabled on this device.",
                        "removed" => "No system apps have been removed for this user.",
                        _ => "This list is empty on this device.",
                    });
            else EmptyState.Visibility = Visibility.Collapsed;
        }
        UpdateHeader();
    }

    void FilterBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _filter = sender.SelectedItem?.Tag as string ?? "all";
        if (IsLoaded) ApplyFilter();
    }

    void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchDebounce ??= new DispatcherTimerLite(TimeSpan.FromMilliseconds(180), ApplyFilter);
        _searchDebounce.Kick();
    }

    void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        foreach (AppInfo a in e.RemovedItems) _selected.Remove(a.Package);
        foreach (AppInfo a in e.AddedItems) _selected.Add(a.Package);
        if (e.AddedItems.LastOrDefault() is AppInfo last) _ = ShowDetails(last);
        UpdateHeader();
    }

    async Task ShowDetails(AppInfo a)
    {
        var d = _source;
        if (d == null) return;
        DetailPkg.Text = a.Package;
        DetailItems.ItemsSource = null;
        DetailLoading.IsActive = true;
        try
        {
            var facts = await Task.Run(() => PackageOps.Details(d, a.Package));
            if (DetailPkg.Text != a.Package) return;
            var list = facts.Select(kv => new Fact(kv.Key, kv.Value)).ToList();
            list.Insert(0, new Fact("Type", a.KindText + (a.StateText.Length > 0 ? $", {a.StateText.ToLowerInvariant()}" : "")));
            if (a.ApkPath.Length > 0) list.Add(new Fact("APK path", a.ApkPath));
            DetailItems.ItemsSource = list;
        }
        finally { DetailLoading.IsActive = false; }
    }

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (AppList.ItemsSource is List<AppInfo> shown)
        {
            foreach (var a in shown) _selected.Add(a.Package);
            ApplyFilter();
        }
    }

    void ClearSel_Click(object sender, RoutedEventArgs e)
    {
        _selected.Clear();
        ApplyFilter();
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = Load();

    // ---- Actions -------------------------------------------------------

    List<string> Picked() => _selected.OrderBy(p => p, StringComparer.Ordinal).ToList();

    static string Describe(List<string> pkgs, IReadOnlyList<Device> targets)
    {
        var what = pkgs.Count == 1 ? pkgs[0] : $"{pkgs.Count} apps";
        var where = targets.Count == 1 ? targets[0].DisplayName : $"{targets.Count} devices";
        return $"{what} on {where}";
    }

    async Task<bool> GuardCritical(List<string> pkgs, string verb)
    {
        var critical = pkgs.Where(PackageOps.Critical.Contains).ToList();
        if (critical.Count == 0) return true;
        return await Dialogs.Confirm($"{verb} core system apps?",
            $"{string.Join(", ", critical)} {(critical.Count == 1 ? "is" : "are")} needed for the device to work normally. " +
            $"Doing this can leave the device stuck or unusable until a factory reset.", $"{verb} anyway", destructive: true);
    }

    async Task RunAction(string name, Func<Device, string, Task<string?>> op, bool reload = true)
    {
        var pkgs = Picked();
        var targets = Fleet.Devices.Targets;
        if (pkgs.Count == 0 || targets.Count == 0) return;
        await Batch.RunItems(name, targets, pkgs, op);
        if (reload) await Load();
    }

    async void Disable_Click(object sender, RoutedEventArgs e)
    {
        var pkgs = Picked();
        if (!await GuardCritical(pkgs, "Disable")) return;
        await RunAction("Disable", PackageOps.Disable);
    }

    async void Enable_Click(object sender, RoutedEventArgs e) => await RunAction("Enable", PackageOps.Enable);

    async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var pkgs = Picked();
        var targets = Fleet.Devices.Targets;
        if (!await GuardCritical(pkgs, "Uninstall")) return;
        var keep = new CheckBox { Content = "Keep the app's data so it can be restored later" };
        var anySystem = _all.Any(a => a.IsSystem && _selected.Contains(a.Package));
        var body = $"This removes {Describe(pkgs, targets)} for the current user." +
                   (anySystem ? " System apps stay on the device and can be brought back with Restore." : "");
        if (!await Dialogs.Confirm("Uninstall apps?", body, "Uninstall", destructive: true, extra: keep)) return;
        var k = keep.IsChecked == true;
        await RunAction("Uninstall", (d, p) => PackageOps.Uninstall(d, p, k));
    }

    async void Restore_Click(object sender, RoutedEventArgs e) => await RunAction("Restore", PackageOps.Restore);

    async void Clear_Click(object sender, RoutedEventArgs e)
    {
        var pkgs = Picked();
        if (!await Dialogs.Confirm("Clear app data?",
                $"This deletes all data, settings and sign-ins for {Describe(pkgs, Fleet.Devices.Targets)}. It can't be undone.",
                "Clear data", destructive: true)) return;
        await RunAction("Clear data", PackageOps.ClearData, reload: false);
    }

    async void ForceStop_Click(object sender, RoutedEventArgs e) => await RunAction("Force stop", PackageOps.ForceStop, reload: false);
    async void Launch_Click(object sender, RoutedEventArgs e) => await RunAction("Launch", PackageOps.Launch, reload: false);

    async void SaveApk_Click(object sender, RoutedEventArgs e)
    {
        await RunAction("Save APK", PackageOps.SaveApk, reload: false);
        Fleet.Log.Add(null, $"Saved APKs are in {Fleet.Settings.DownloadsDir}");
        Dialogs.OpenFolder(Fleet.Settings.DownloadsDir);
    }

    // ---- Presets -------------------------------------------------------

    void PresetMenu_Opening(object? sender, object e)
    {
        PresetMenu.Items.Clear();
        var save = new MenuFlyoutItem { Text = "Save selection as preset", Icon = new SymbolIcon(Symbol.Save), IsEnabled = _selected.Count > 0 };
        save.Click += async (_, _) => await SavePreset();
        PresetMenu.Items.Add(save);

        var presets = Fleet.Settings.Value.Presets;
        if (presets.Count == 0) return;
        PresetMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var p in presets)
        {
            var item = new MenuFlyoutItem { Text = $"Select \"{p.Name}\" ({p.Packages.Count})" };
            item.Click += (_, _) => ApplyPreset(p);
            PresetMenu.Items.Add(item);
        }
        var del = new MenuFlyoutSubItem { Text = "Delete preset", Icon = new SymbolIcon(Symbol.Delete) };
        foreach (var p in presets)
        {
            var item = new MenuFlyoutItem { Text = p.Name };
            item.Click += (_, _) => { Fleet.Settings.Value.Presets.Remove(p); Fleet.Settings.Save(); };
            del.Items.Add(item);
        }
        PresetMenu.Items.Add(new MenuFlyoutSeparator());
        PresetMenu.Items.Add(del);
    }

    async Task SavePreset()
    {
        var name = await Dialogs.Prompt("Save preset", "Preset name", _source != null ? $"{_source.DisplayName} cleanup" : "");
        if (name == null) return;
        var presets = Fleet.Settings.Value.Presets;
        presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        presets.Add(new Preset { Name = name, Packages = Picked() });
        Fleet.Settings.Save();
        Fleet.Log.Success(null, $"Saved preset \"{name}\" with {_selected.Count} apps");
    }

    void ApplyPreset(Preset p)
    {
        _selected.Clear();
        var present = _all.Select(a => a.Package).ToHashSet(StringComparer.Ordinal);
        foreach (var pkg in p.Packages) if (present.Contains(pkg)) _selected.Add(pkg);
        var missing = p.Packages.Count - _selected.Count;
        FilterBar.SelectedItem = FilterBar.Items[^1]; // All
        _filter = "all";
        ApplyFilter();
        Fleet.Log.Add(null, $"Selected {_selected.Count} apps from \"{p.Name}\"" + (missing > 0 ? $"; {missing} aren't on this device" : ""));
    }
}

/// <summary>Restartable one-shot timer for debouncing on the UI thread.</summary>
public sealed class DispatcherTimerLite
{
    readonly DispatcherTimer _t;
    public DispatcherTimerLite(TimeSpan delay, Action action)
    {
        _t = new DispatcherTimer { Interval = delay };
        _t.Tick += (_, _) => { _t.Stop(); action(); };
    }
    public void Kick() { _t.Stop(); _t.Start(); }
}
