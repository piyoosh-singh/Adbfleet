using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AdbFleet.Views;

public sealed partial class SettingsPage : Page
{
    bool _init;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _init = true;
        var s = Fleet.Settings.Value;
        AdbPath.Text = s.AdbPath;
        ScrcpyPath.Text = s.ScrcpyPath;
        DownloadsDir.Text = Fleet.Settings.DownloadsDir;
        ShotsDir.Text = Fleet.Settings.ScreenshotsDir;
        UserId.Value = s.UserId;
        ThemeChoice.SelectedIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        var v = typeof(App).Assembly.GetName().Version;
        AboutText.Text = Fleet.IsPortable
            ? $"AdbFleet {v?.ToString(3)}, portable. Settings, logs and tools stay in {Fleet.AppDir}"
            : $"AdbFleet {v?.ToString(3)}. Settings are stored in {Fleet.DataDir}.";
        _init = false;
        _ = RefreshStatus();
    }

    async Task RefreshStatus()
    {
        ToolLocator.Reset();
        var adb = Fleet.Adb.AdbPath;
        if (adb == null) AdbStatus.Text = "adb.exe wasn't found. Download platform-tools or choose an adb.exe.";
        else
        {
            AdbStatus.Text = $"Using {adb}";
            try
            {
                var r = await Fleet.Adb.RunAsync(null, ["version"], default, 10_000);
                var line = r.StdOut.Split('\n').FirstOrDefault(l => l.Contains("Version")) ?? r.FirstLine;
                AdbStatus.Text = $"Using {adb}  ({line.Trim()})";
            }
            catch { }
        }
        var sc = ToolLocator.FindScrcpy();
        ScrcpyStatus.Text = sc == null ? "scrcpy wasn't found. Download it to mirror device screens." : $"Using {sc}";
    }

    void AdbPath_LostFocus(object sender, RoutedEventArgs e)
    {
        var p = AdbPath.Text.Trim().Trim('"');
        if (p.Length > 0 && !File.Exists(p)) { AdbStatus.Text = $"No file at {p}"; return; }
        Fleet.Settings.Value.AdbPath = p;
        Fleet.Settings.Save();
        _ = RefreshStatus();
    }

    void ScrcpyPath_LostFocus(object sender, RoutedEventArgs e)
    {
        var p = ScrcpyPath.Text.Trim().Trim('"');
        if (p.Length > 0 && !File.Exists(p)) { ScrcpyStatus.Text = $"No file at {p}"; return; }
        Fleet.Settings.Value.ScrcpyPath = p;
        Fleet.Settings.Save();
        _ = RefreshStatus();
    }

    async void BrowseAdb_Click(object sender, RoutedEventArgs e)
    {
        var f = await Dialogs.PickFile(".exe");
        if (f == null) return;
        AdbPath.Text = f;
        AdbPath_LostFocus(sender, e);
        Fleet.Devices.Start();
    }

    async void BrowseScrcpy_Click(object sender, RoutedEventArgs e)
    {
        var f = await Dialogs.PickFile(".exe");
        if (f == null) return;
        ScrcpyPath.Text = f;
        ScrcpyPath_LostFocus(sender, e);
    }

    async void DownloadAdb_Click(object sender, RoutedEventArgs e)
    {
        DownloadAdb.IsEnabled = false;
        try
        {
            var path = await Task.Run(() => Downloader.PlatformTools(new Progress<string>(m => AdbStatus.Text = m)));
            AdbPath.Text = path;
            Fleet.Devices.Start();
        }
        catch (Exception ex) { AdbStatus.Text = "Download failed: " + ex.Message; }
        finally { DownloadAdb.IsEnabled = true; await RefreshStatus(); }
    }

    async void DownloadScrcpy_Click(object sender, RoutedEventArgs e)
    {
        DownloadScrcpy.IsEnabled = false;
        try
        {
            var path = await Task.Run(() => Downloader.Scrcpy(new Progress<string>(m => ScrcpyStatus.Text = m)));
            ScrcpyPath.Text = path;
        }
        catch (Exception ex) { ScrcpyStatus.Text = "Download failed: " + ex.Message; }
        finally { DownloadScrcpy.IsEnabled = true; await RefreshStatus(); }
    }

    async void RestartAdb_Click(object sender, RoutedEventArgs e)
    {
        await Fleet.ExecWithTimeout(null, 15_000, "kill-server");
        Fleet.Devices.Start();
        await RefreshStatus();
    }

    async void ChangeDownloads_Click(object sender, RoutedEventArgs e)
    {
        var d = await Dialogs.PickFolder();
        if (d == null) return;
        Fleet.Settings.Value.DownloadsDir = d;
        Fleet.Settings.Save();
        DownloadsDir.Text = d;
    }

    async void ChangeShots_Click(object sender, RoutedEventArgs e)
    {
        var d = await Dialogs.PickFolder();
        if (d == null) return;
        Fleet.Settings.Value.ScreenshotsDir = d;
        Fleet.Settings.Save();
        ShotsDir.Text = d;
    }

    void UserId_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_init || double.IsNaN(args.NewValue)) return;
        Fleet.Settings.Value.UserId = (int)args.NewValue;
        Fleet.Settings.Save();
    }

    void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_init) return;
        var t = ThemeChoice.SelectedItem as string ?? "System";
        Fleet.Settings.Value.Theme = t;
        Fleet.Settings.Save();
        Fleet.Window.ApplyTheme(t);
    }
}
