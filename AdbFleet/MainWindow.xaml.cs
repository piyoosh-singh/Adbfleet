using AdbFleet.Models;
using AdbFleet.Services;
using AdbFleet.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;

namespace AdbFleet;

public sealed partial class MainWindow : Window
{
    public DeviceService Devices => Fleet.Devices;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AdbFleet.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1360, 860));
        ApplyTheme(Fleet.Settings.Value.Theme);

        Devices.TargetsChanged += () => DispatcherQueue.TryEnqueue(RefreshTargets);
        Devices.All.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            EmptyRail.Visibility = Devices.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (DeviceList.SelectedItem == null && Devices.Focused != null) DeviceList.SelectedItem = Devices.Focused;
        });
        Devices.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceService.Focused) && DeviceList.SelectedItem != Devices.Focused)
                DeviceList.SelectedItem = Devices.Focused;
        };
        EmptyRail.Visibility = Visibility.Visible;
        Nav.SelectedItem = Nav.MenuItems[0];
        RefreshTargets();
    }

    public void ApplyTheme(string theme)
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }

    void RefreshTargets()
    {
        var t = Devices.Targets;
        TargetChips.ItemsSource = t;
        TargetText.Text = Devices.Checked.Count > 0
            ? (t.Count == 1 ? "Actions run on 1 checked device" : $"Actions run on {t.Count} checked devices")
            : t.Count == 1 ? "Actions run on the selected device. Check devices to act on several at once"
            : "No device is ready";
    }

    void DeviceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is Device d) Devices.Focused = d;
    }

    void CheckAll_Click(object sender, RoutedEventArgs e) => Devices.CheckAll(true);
    void UncheckAll_Click(object sender, RoutedEventArgs e) => Devices.CheckAll(false);

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ConnectDialog { XamlRoot = Content.XamlRoot };
        await dlg.ShowAsync();
    }

    static Device? DeviceOf(object sender) => (sender as FrameworkElement)?.DataContext as Device;

    void DeviceMore_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceOf(sender) is { } d) BuildDeviceMenu(d).ShowAt((FrameworkElement)sender);
    }

    void DeviceRow_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (DeviceOf(sender) is not { } d) return;
        BuildDeviceMenu(d).ShowAt((FrameworkElement)sender, e.GetPosition((FrameworkElement)sender));
        e.Handled = true;
    }

    async void DeviceRow_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (DeviceOf(sender) is { IsAbsent: true } d) await Batch.Run("Reconnect", [d], Devices.Reconnect);
    }

    MenuFlyout BuildDeviceMenu(Device d)
    {
        var menu = new MenuFlyout();
        void Add(string text, string glyph, Func<Task> action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph }, IsEnabled = enabled };
            item.Click += async (_, _) => await action();
            menu.Items.Add(item);
        }

        if (d.IsAbsent && d.IsWifi)
            Add("Reconnect", "", () => Batch.Run("Reconnect", [d], Devices.Reconnect), d.ConnectAddress != null);
        if (!d.IsAbsent && !d.IsWifi)
            Add("Switch to Wi-Fi debugging", "", () => Batch.Run("Switch to Wi-Fi", [d], DeviceOps.SwitchToWifi), d.IsOnline);
        if (!d.IsAbsent && d.IsWifi)
            Add("Disconnect", "", async () => await DeviceOps.Disconnect(d));

        if (d.HasProxy)
            Add($"Remove proxy ({d.Proxy})", "\uE774", () => Batch.Run("Remove proxy", [d], ProxyOps.Clear), d.IsOnline);

        var addr = d.Address.Length > 0 ? d.Address : d.Serial;
        Add($"Copy address ({addr})", "", () => { Copy(addr); return Task.CompletedTask; });

        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Remove from list", "", () => ConfirmRemove(d));
        return menu;
    }

    static void Copy(string text)
    {
        var p = new DataPackage();
        p.SetText(text);
        Clipboard.SetContent(p);
    }

    async Task ConfirmRemove(Device d)
    {
        var body = d.IsAbsent
            ? $"{d.DisplayName} ({d.AddressText}) will be forgotten. Connect it again any time to add it back."
            : d.IsWifi
                ? $"{d.DisplayName} will be disconnected and forgotten. Connect it again any time to add it back."
                : $"{d.DisplayName} is plugged in over USB. It will be hidden until you unplug it, and forgotten after that.";
        if (await Dialogs.Confirm("Remove device?", body, "Remove"))
            await Devices.Remove(d);
    }

    async void RefreshAvailability_Click(object sender, RoutedEventArgs e)
    {
        RefreshButton.IsEnabled = false;
        RefreshIcon.Visibility = Visibility.Collapsed;
        RefreshRing.IsActive = true;
        try { await Devices.RefreshAvailability(reconnect: true, quiet: false); }
        finally
        {
            RefreshRing.IsActive = false;
            RefreshIcon.Visibility = Visibility.Visible;
            RefreshButton.IsEnabled = true;
        }
    }

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type page = args.IsSettingsSelected ? typeof(SettingsPage) : ((args.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "overview" => typeof(OverviewPage),
            "install" => typeof(InstallPage),
            "tools" => typeof(ToolsPage),
            "console" => typeof(ConsolePage),
            "processes" => typeof(ProcessesPage),
            _ => typeof(AppsPage),
        };
        ContentFrame.Navigate(page, null, new SuppressNavigationTransitionInfo());
    }
}
