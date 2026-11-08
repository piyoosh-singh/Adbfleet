using System.Collections.ObjectModel;
using AdbFleet.Models;
using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace AdbFleet.Views;

public sealed partial class InstallPage : Page
{
    static readonly string[] Accepted = [".apk", ".apks", ".xapk", ".apkm", ".zip"];
    readonly ObservableCollection<QueuedFile> _queue = new();
    bool _busy;

    public InstallPage()
    {
        InitializeComponent();
        QueueList.ItemsSource = _queue;
        _queue.CollectionChanged += (_, _) => Refresh();
        OptGrant.IsChecked = Fleet.Settings.Value.GrantPermissionsOnInstall;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Fleet.Devices.TargetsChanged += OnTargets;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Fleet.Devices.TargetsChanged -= OnTargets;

    void OnTargets() => DispatcherQueue.TryEnqueue(Refresh);

    void Refresh()
    {
        var has = _queue.Count > 0;
        QueueList.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DropHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        var n = Fleet.Devices.Targets.Count;
        QueueText.Text = has ? $"{_queue.Count} file{(_queue.Count == 1 ? "" : "s")}, {SizeOf(_queue.Sum(q => q.Size))}" : "";
        InstallLabel.Text = _busy ? "Installing" : n <= 1 ? "Install" : $"Install on {n} devices";
        InstallButton.IsEnabled = has && n > 0 && !_busy;
    }

    static string SizeOf(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1024.0:0} KB";

    void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                Add(Directory.EnumerateFiles(p).Where(f => Accepted.Contains(Path.GetExtension(f).ToLowerInvariant())));
                continue;
            }
            if (!File.Exists(p) || !Accepted.Contains(Path.GetExtension(p).ToLowerInvariant())) continue;
            if (_queue.Any(q => string.Equals(q.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            _queue.Add(new QueuedFile { Path = p, Size = new FileInfo(p).Length });
        }
        // A folder of base + split APKs is almost always one app.
        var loose = _queue.Where(q => !q.IsBundle).ToList();
        if (loose.Count > 1 && loose.Any(q => q.FileName.StartsWith("split_", StringComparison.OrdinalIgnoreCase) || q.FileName.StartsWith("config.", StringComparison.OrdinalIgnoreCase)))
            OptSplit.IsChecked = true;
    }

    async void AddFiles_Click(object sender, RoutedEventArgs e) => Add(await Dialogs.PickFiles(Accepted));

    void ClearQueue_Click(object sender, RoutedEventArgs e) { if (!_busy) _queue.Clear(); }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as FrameworkElement)?.Tag is not string path) return;
        var item = _queue.FirstOrDefault(q => q.Path == path);
        if (item != null) _queue.Remove(item);
    }

    void Drop_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to install list";
        DropZone.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    void Drop_DragLeave(object sender, DragEventArgs e) =>
        DropZone.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];

    async void Drop_Drop(object sender, DragEventArgs e)
    {
        Drop_DragLeave(sender, e);
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        Add(items.Select(i => i.Path));
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        var targets = Fleet.Devices.Targets;
        if (_queue.Count == 0 || targets.Count == 0 || _busy) return;
        _busy = true;
        InstallRing.IsActive = true;
        Refresh();
        var opts = new InstallOptions(OptReplace.IsChecked == true, OptDowngrade.IsChecked == true, OptGrant.IsChecked == true,
            OptBypass.IsChecked == true, OptNoStream.IsChecked == true, OptAllUsers.IsChecked == true);
        Fleet.Settings.Value.GrantPermissionsOnInstall = opts.GrantAll;
        var split = OptSplit.IsChecked == true;
        var files = _queue.ToList();
        ResultList.ItemsSource = targets;
        try
        {
            ResultText.Text = "Preparing files";
            var units = await Task.Run(() => InstallOps.Prepare(files, split));
            ResultText.Text = $"Installing {units.Count} app{(units.Count == 1 ? "" : "s")} on {targets.Count} device{(targets.Count == 1 ? "" : "s")}";
            var summary = await Batch.RunItems("Install", targets, units, (d, u) => InstallOps.Install(d, u, opts));
            ResultText.Text = summary.Failed == 0
                ? $"Installed on all {summary.Total} device{(summary.Total == 1 ? "" : "s")}."
                : $"Installed on {summary.Ok} of {summary.Total} devices. Hover a red mark to see why it failed.";
            if (summary.Failed == 0) _queue.Clear();
        }
        catch (Exception ex)
        {
            ResultText.Text = ex.Message;
            Fleet.Log.Error(null, "Install: " + ex.Message);
        }
        finally
        {
            _busy = false;
            InstallRing.IsActive = false;
            Fleet.Settings.Save();
            Refresh();
        }
    }
}
