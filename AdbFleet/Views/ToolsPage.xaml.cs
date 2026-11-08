using System.ComponentModel;
using System.Text.RegularExpressions;
using AdbFleet.Models;
using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace AdbFleet.Views;

public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();
        ScrcpyArgs.Text = Fleet.Settings.Value.ScrcpyArgs;
        ProxyPort.Value = Fleet.Settings.Value.ProxyPort;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Fleet.Devices.TargetsChanged += OnTargets;
        var exe = ToolLocator.FindScrcpy();
        ScrcpyStatus.Text = exe == null
            ? "scrcpy isn't installed yet. Download it from Settings to mirror and control device screens."
            : $"Using {exe}";
        FillPcAddresses();
        BindProxyList(checkAll: true);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Fleet.Devices.TargetsChanged -= OnTargets;
    }

    // Reload once the device's details (name, Android version) have arrived.
    void OnTargets() => DispatcherQueue.TryEnqueue(() =>
    {
        BindProxyList(checkAll: false);
    });

    static IReadOnlyList<Device> T => Fleet.Devices.Targets;

    async void Key_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        var label = key.Replace("KEYCODE_", "").Replace('_', ' ').ToLowerInvariant();
        await Batch.Run($"Press {label}", T, d => DeviceOps.Key(d, key));
    }

    async void SendText_Click(object sender, RoutedEventArgs e)
    {
        var text = TypeBox.Text;
        if (text.Length == 0) return;
        await Batch.Run("Send text", T, d => DeviceOps.Text(d, text));
        TypeBox.Text = "";
    }

    void TypeBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) SendText_Click(sender, e);
    }

    async void ApplyScreen_Click(object sender, RoutedEventArgs e)
    {
        var size = SizeBox.Text.Trim().Replace('×', 'x').Replace('*', 'x');
        var dens = DensityBox.Text.Trim();
        if (size.Length > 0 && !Regex.IsMatch(size, @"^\d{3,5}x\d{3,5}$"))
        {
            Fleet.Log.Error(null, "Resolution must look like 1920x1080.");
            return;
        }
        if (dens.Length > 0 && !Regex.IsMatch(dens, @"^\d{2,4}$"))
        {
            Fleet.Log.Error(null, "Density must be a number such as 320.");
            return;
        }
        if (size.Length == 0 && dens.Length == 0) return;
        await Batch.Run("Change display", T, d => DeviceOps.SetScreen(d, size.Length > 0 ? size : null, dens.Length > 0 ? dens : null));
    }

    async void ResetScreen_Click(object sender, RoutedEventArgs e)
        => await Batch.Run("Reset display", T, d => DeviceOps.SetScreen(d, "reset", "reset"));

    async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        var item = (ComboBoxItem)RebootMode.SelectedItem;
        var mode = item.Tag as string ?? "";
        var label = (string)item.Content;
        var targets = T;
        if (targets.Count == 0) return;
        var where = targets.Count == 1 ? targets[0].DisplayName : $"{targets.Count} devices";
        if (!await Dialogs.Confirm($"{label}?", $"{label} {where} now. Unsaved work on the device will be lost.", label)) return;
        await Batch.Run(label, targets, d => DeviceOps.Reboot(d, mode));
    }

    void ScrcpyArgs_LostFocus(object sender, RoutedEventArgs e)
    {
        Fleet.Settings.Value.ScrcpyArgs = ScrcpyArgs.Text.Trim();
        Fleet.Settings.Save();
    }

    async void Mirror_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string ?? "";
        var baseArgs = ScrcpyArgs.Text.Trim();
        await Batch.Run(tag == "record" ? "Mirror and record" : tag.Length > 0 ? "Camera" : "Mirror screen", T, d =>
        {
            var args = baseArgs;
            if (tag == "record")
            {
                var file = Path.Combine(Fleet.Settings.ScreenshotsDir, $"{d.SafeName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
                args += $" --record \"{file}\"";
            }
            else if (tag.Length > 0) args += " " + tag;
            return Task.FromResult(DeviceOps.LaunchScrcpy(d, args));
        });
    }

    async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        string? last = null;
        await Batch.Run("Screenshot", T, d => DeviceOps.Screenshot(d, f => last = f));
        if (last != null && T.Count == 1)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(last) { UseShellExecute = true }); } catch { }
        }
        else if (last != null) Dialogs.OpenFolder(Fleet.Settings.ScreenshotsDir);
    }

    void OpenShots_Click(object sender, RoutedEventArgs e) => Dialogs.OpenFolder(Fleet.Settings.ScreenshotsDir);

    async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        var usb = T.Where(d => !d.IsWifi).ToList();
        if (usb.Count == 0)
        {
            Fleet.Log.Add(null, "Every selected device is already on Wi-Fi.");
            return;
        }
        await Batch.Run("Switch to Wi-Fi", usb, DeviceOps.SwitchToWifi);
    }


    // ---- Proxy ----

    List<PcAddress> _pc = new();

    void FillPcAddresses()
    {
        _pc = ProxyOps.PcAddresses();
        var typed = ProxyHost.Text?.Trim() ?? "";
        ProxyHost.ItemsSource = _pc.Select(a => a.Ip).ToList();
        var best = ProxyOps.BestFor(T, _pc);
        // Keep an address the user typed; otherwise suggest this PC's address on the devices' network.
        if (typed.Length > 0 && !_pc.Any(a => a.Ip == typed)) ProxyHost.Text = typed;
        else if (best != null) ProxyHost.SelectedItem = best.Ip;
        UpdateProxyHint();
    }

    string HostValue()
    {
        var t = (ProxyHost.Text ?? "").Trim();
        return t.Length > 0 ? t : (ProxyHost.SelectedItem as string ?? "");
    }

    void ProxyHost_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateProxyHint(ProxyHost.SelectedItem as string);

    void UpdateProxyHint(string? selected = null)
    {
        var host = selected ?? HostValue();
        var pc = _pc.FirstOrDefault(a => a.Ip == host);
        var where = pc != null ? $"{host} is this PC ({pc.Adapter}). " : _pc.Count == 0 ? "This PC has no network address right now. " : "";
        ProxyHint.Text = where +
            "Sets Android's global HTTP proxy on the selected devices, so most apps send web traffic through it. " +
            "To inspect HTTPS, the proxy's certificate must also be trusted on the device. " +
            "A proxy entered for one Wi-Fi network in Android's own settings isn't shown here.";
    }

    void BindProxyList(bool checkAll)
    {
        var targets = T.ToList();
        ProxyList.ItemsSource = targets;
        ProxyEmpty.Visibility = targets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var toCheck = targets.Where(d => d.IsOnline && (checkAll || d.Proxy == null)).ToList();
        if (toCheck.Count > 0) _ = ProxyOps.RefreshAll(toCheck);
    }

    async void ProxyCheck_Click(object sender, RoutedEventArgs e)
    {
        FillPcAddresses();
        await Batch.Run("Check proxy", T.Where(d => d.IsOnline).ToList(), async d =>
        {
            var err = await ProxyOps.Refresh(d);
            if (err == null) Fleet.Log.Add(d, d.HasProxy ? $"Proxy: {d.Proxy}" : "Proxy: none");
            return err;
        });
    }

    async void ProxySet_Click(object sender, RoutedEventArgs e)
    {
        var host = HostValue();
        var err = ProxyOps.Validate(host, ProxyPort.Value);
        if (err != null)
        {
            ProxyHint.Text = err;
            Fleet.Log.Error(null, err);
            return;
        }
        var port = (int)ProxyPort.Value;
        Fleet.Settings.Value.ProxyPort = port;
        Fleet.Settings.Save();
        UpdateProxyHint();
        await Batch.Run($"Set proxy {host}:{port}", T, d => ProxyOps.Set(d, host, port));
    }

    async void ProxyRemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var targets = T.Where(d => d.Proxy != "").ToList();
        if (targets.Count == 0)
        {
            Fleet.Log.Add(null, "None of the selected devices uses a proxy.");
            return;
        }
        await Batch.Run("Remove proxy", targets, ProxyOps.Clear);
    }

    async void ProxyRemoveOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Device d)
            await Batch.Run("Remove proxy", [d], ProxyOps.Clear);
    }
}
