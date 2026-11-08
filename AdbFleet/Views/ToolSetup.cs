using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AdbFleet.Views;

/// <summary>
/// AdbFleet doesn't ship adb or scrcpy. When one is missing, this asks the user to download the
/// official build or point to a copy they already have.
/// </summary>
public static class ToolSetup
{
    static bool _open;

    /// <summary>Asks for adb if it can't be found. Returns true once adb is available.</summary>
    public static Task<bool> EnsureAdb() => Ensure(new Tool(
        Name: "adb",
        Title: "Set up adb",
        Body: "AdbFleet talks to your devices through adb, Google's Android Debug Bridge. It isn't included with AdbFleet. " +
              "Download Google's official platform-tools, or choose an adb.exe you already have, for example from Android Studio.",
        LicenseText: "Android SDK Platform-Tools license",
        LicenseUrl: "https://developer.android.com/studio/releases/platform-tools",
        ExeName: "adb.exe",
        Find: () => { ToolLocator.Reset(); return ToolLocator.FindAdb(); },
        Download: p => Downloader.PlatformTools(p),
        Save: path => Fleet.Settings.Value.AdbPath = path,
        AfterReady: () => Fleet.Devices.Start()));

    /// <summary>Asks for scrcpy if it can't be found. Returns true once scrcpy is available.</summary>
    public static Task<bool> EnsureScrcpy() => Ensure(new Tool(
        Name: "scrcpy",
        Title: "Set up screen mirroring",
        Body: "Screen mirroring uses scrcpy, a free open-source tool by Genymobile. It isn't included with AdbFleet. " +
              "Download the latest release from GitHub, or choose a scrcpy.exe you already have.",
        LicenseText: "scrcpy on GitHub (Apache 2.0)",
        LicenseUrl: "https://github.com/Genymobile/scrcpy",
        ExeName: "scrcpy.exe",
        Find: ToolLocator.FindScrcpy,
        Download: p => Downloader.Scrcpy(p),
        Save: path => Fleet.Settings.Value.ScrcpyPath = path,
        AfterReady: null));

    sealed record Tool(string Name, string Title, string Body, string LicenseText, string LicenseUrl, string ExeName,
        Func<string?> Find, Func<IProgress<string>, Task<string>> Download, Action<string> Save, Action? AfterReady);

    static async Task<bool> Ensure(Tool t)
    {
        if (t.Find() != null) return true;
        if (_open) return false; // only one setup dialog at a time
        _open = true;
        try { return await Show(t); }
        finally { _open = false; }
    }

    static async Task<bool> Show(Tool t)
    {
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        var link = new HyperlinkButton { Content = t.LicenseText, NavigateUri = new Uri(t.LicenseUrl), Padding = new Thickness(0) };
        var where = new TextBlock
        {
            Style = (Style)Application.Current.Resources["Hint"],
            Text = Fleet.IsPortable
                ? $"Downloads go into the {(t.Name == "adb" ? "platform-tools" : "scrcpy")} folder beside AdbFleet.exe, so they travel with the portable folder."
                : $"Downloads go into {Fleet.DataDir}.",
        };
        var content = new StackPanel { Spacing = 12, MaxWidth = 460 };
        content.Children.Add(new TextBlock { Text = t.Body, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(where);
        content.Children.Add(link);
        content.Children.Add(progress);
        content.Children.Add(status);

        var dlg = new ContentDialog
        {
            XamlRoot = Fleet.XamlRoot,
            Title = t.Title,
            Content = content,
            PrimaryButtonText = "Download",
            SecondaryButtonText = $"Choose {t.ExeName}",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
        };

        var ready = false;
        void Fail(string message)
        {
            progress.Visibility = Visibility.Collapsed;
            status.Visibility = Visibility.Visible;
            status.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            status.Text = message;
            dlg.IsPrimaryButtonEnabled = dlg.IsSecondaryButtonEnabled = true;
        }

        dlg.PrimaryButtonClick += async (_, e) =>
        {
            // Keep the dialog open while downloading, showing progress.
            var deferral = e.GetDeferral();
            e.Cancel = true;
            dlg.IsPrimaryButtonEnabled = dlg.IsSecondaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            status.Visibility = Visibility.Visible;
            status.ClearValue(TextBlock.ForegroundProperty);
            try
            {
                var p = new Progress<string>(m => status.Text = m + "…");
                await Task.Run(() => t.Download(p));
                ready = t.Find() != null;
                if (ready) { deferral.Complete(); dlg.Hide(); return; }
                Fail($"The download finished but {t.ExeName} still wasn't found.");
            }
            catch (Exception ex) { Fail($"Download failed: {ex.Message}. Check your internet connection, or choose {t.ExeName} instead."); }
            deferral.Complete();
        };

        dlg.SecondaryButtonClick += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.Cancel = true;
            var file = await Dialogs.PickFile(".exe");
            if (file != null)
            {
                if (!string.Equals(Path.GetFileName(file), t.ExeName, StringComparison.OrdinalIgnoreCase))
                    Fail($"That file is {Path.GetFileName(file)}. Choose {t.ExeName}.");
                else
                {
                    t.Save(file);
                    Fleet.Settings.Save();
                    ready = t.Find() != null;
                    if (ready) { deferral.Complete(); dlg.Hide(); return; }
                    Fail($"{t.ExeName} couldn't be used from that location.");
                }
            }
            deferral.Complete();
        };

        await dlg.ShowAsync();
        if (ready)
        {
            Fleet.Log.Success(null, $"{t.Name} is ready: {t.Find()}");
            t.AfterReady?.Invoke();
        }
        else if (t.Name == "adb")
            Fleet.Log.Add(null, "adb isn't set up yet. Open Settings to download it or choose adb.exe.");
        return ready;
    }
}
