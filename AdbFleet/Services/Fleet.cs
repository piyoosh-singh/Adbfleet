using AdbFleet.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AdbFleet.Services;

/// <summary>Process-wide services. Kept deliberately small: no DI container needed for one window.</summary>
public static class Fleet
{
    /// <summary>
    /// Folder that holds AdbFleet.exe. Not AppContext.BaseDirectory: a single-file build runs
    /// from a temporary extraction folder, but portable data must sit beside the real exe.
    /// </summary>
    public static readonly string AppDir =
        (Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    /// <summary>
    /// Portable mode: a "portable.txt" next to the exe keeps settings, logs, downloads and
    /// screenshots beside the app, so the whole folder can live on a USB stick.
    /// </summary>
    public static readonly bool IsPortable = File.Exists(Path.Combine(AppDir, "portable.txt"));

    public static readonly string DataDir = IsPortable
        ? Path.Combine(AppDir, "data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbFleet");

    public static DispatcherQueue UI { get; set; } = null!;
    public static MainWindow Window { get; set; } = null!;
    public static XamlRoot XamlRoot => Window.Content.XamlRoot;
    public static IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(Window);

    public static readonly SettingsStore Settings = new();
    public static readonly AdbClient Adb = new();
    public static readonly LogService Log = new();
    public static readonly DeviceService Devices = new();

    public static int User => Settings.Value.UserId;

    public static Task OnUI(Action a)
    {
        if (UI.HasThreadAccess) { a(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource();
        UI.TryEnqueue(() =>
        {
            try { a(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public static Task<T> OnUIGet<T>(Func<T> f)
    {
        if (UI.HasThreadAccess) return Task.FromResult(f());
        var tcs = new TaskCompletionSource<T>();
        UI.TryEnqueue(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Runs an adb command against a device and records the command and its output in the activity log.</summary>
    public static async Task<AdbResult> Exec(Device? d, params string[] args)
        => await ExecWithTimeout(d, 120_000, args);

    public static async Task<AdbResult> ExecWithTimeout(Device? d, int timeoutMs, params string[] args)
    {
        Log.Add(d, AdbClient.Display(args), LogKind.Command);
        AdbResult r;
        try
        {
            r = await Adb.RunAsync(d?.Serial, args, default, timeoutMs);
        }
        catch (Exception ex)
        {
            Log.Add(d, ex.Message, LogKind.Error);
            return new AdbResult(-1, "", ex.Message);
        }
        var text = r.Text;
        if (text.Length > 0)
        {
            var lines = text.Split('\n');
            var shown = lines.Length > 40 ? string.Join('\n', lines.Take(40)) + $"\n… {lines.Length - 40} more lines" : text;
            Log.Add(d, shown, r.Ok ? LogKind.Output : LogKind.Error);
        }
        return r;
    }
}
