using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbFleet.Views;

public sealed partial class ConnectDialog : ContentDialog
{
    public ConnectDialog()
    {
        InitializeComponent();
        var last = Fleet.Settings.Value.ConnectHistory.FirstOrDefault();
        if (last != null) HostBox.Text = last;
    }

    void Show(InfoBarSeverity sev, string title, string msg)
    {
        Status.Severity = sev;
        Status.Title = title;
        Status.Message = msg;
        Status.IsOpen = true;
    }

    void HostBox_GotFocus(object sender, RoutedEventArgs e)
    {
        HostBox.ItemsSource = Fleet.Settings.Value.ConnectHistory;
        HostBox.IsSuggestionListOpen = Fleet.Settings.Value.ConnectHistory.Count > 0 && HostBox.Text.Length == 0;
    }

    void HostBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = Fleet.Settings.Value.ConnectHistory
            .Where(h => h.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    async void HostBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string s) sender.Text = s;
        await DoConnect();
    }

    async void Connect_Click(object sender, RoutedEventArgs e) => await DoConnect();

    async Task DoConnect()
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { Show(InfoBarSeverity.Warning, "Enter an address", "Type the IP address shown in the device's network settings."); return; }
        Show(InfoBarSeverity.Informational, "Connecting", DeviceOps.NormalizeHost(host));
        var err = await DeviceOps.Connect(host);
        if (err == null) Show(InfoBarSeverity.Success, "Connected", $"{DeviceOps.NormalizeHost(host)} was added to your devices.");
        else Show(InfoBarSeverity.Error, "Couldn't connect", err);
    }

    async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (PairHostBox.Text.Trim().Length == 0 || PairCodeBox.Text.Trim().Length == 0)
        {
            Show(InfoBarSeverity.Warning, "Enter the pairing address and code", "Both are shown on the device's pairing screen.");
            return;
        }
        Show(InfoBarSeverity.Informational, "Pairing", PairHostBox.Text.Trim());
        var err = await DeviceOps.Pair(PairHostBox.Text, PairCodeBox.Text);
        if (err == null)
        {
            Show(InfoBarSeverity.Success, "Paired", "Now enter the address shown under Wireless debugging (not the pairing address) and select Connect.");
            var ip = PairHostBox.Text.Trim().Split(':')[0];
            HostBox.Text = ip + ":";
        }
        else Show(InfoBarSeverity.Error, "Pairing failed", err);
    }

    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        Show(InfoBarSeverity.Informational, "Searching", "Looking for devices with Wireless debugging turned on.");
        var found = await DeviceOps.DiscoverMdns();
        FoundList.ItemsSource = found.Select(f => $"{(f.IsPairing ? "Pairing" : "Connect")}   {f.Address}   {f.Name}").ToList();
        FoundList.Tag = found;
        if (found.Count == 0) Show(InfoBarSeverity.Warning, "Nothing found", "Make sure the device is on the same network and Wireless debugging is on. Some routers block discovery; enter the address manually instead.");
        else Status.IsOpen = false;
    }

    void Found_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (FoundList.Tag is not List<DeviceOps.MdnsService> found || e.ClickedItem is not string label) return;
        var idx = (FoundList.ItemsSource as List<string>)!.IndexOf(label);
        if (idx < 0) return;
        var svc = found[idx];
        if (svc.IsPairing) { PairHostBox.Text = svc.Address; PairCodeBox.Focus(FocusState.Programmatic); }
        else HostBox.Text = svc.Address;
    }
}
