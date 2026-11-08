using AdbFleet.Models;
using AdbFleet.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AdbFleet;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Keep the window alive; a failed adb call should never take the app down.
            Fleet.Log.Error(null, "Unexpected error: " + e.Exception.Message);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Fleet.Log.Error(null, "Background error: " + e.Exception.GetBaseException().Message);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Fleet.UI = DispatcherQueue.GetForCurrentThread();
        Fleet.Settings.Load();
        var window = new MainWindow();
        Fleet.Window = window;
        window.Activate();
        Fleet.Devices.Start();
    }
}
