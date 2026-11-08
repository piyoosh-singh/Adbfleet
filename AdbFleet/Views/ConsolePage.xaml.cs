using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using AdbFleet.Models;
using AdbFleet.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AdbFleet.Views;

public sealed class LogcatLine
{
    public required string Text { get; init; }
    public required Brush Brush { get; init; }
}

public sealed partial class ConsolePage : Page
{
    // ---- Activity ----
    readonly DispatcherTimerLite _filterDebounce;
    bool _scrollPending;

    // ---- Logcat ----
    const int LogcatCap = 20000;
    readonly ObservableCollection<LogcatLine> _logcatView = new();
    readonly List<string> _logcatRaw = new();
    readonly ConcurrentQueue<string> _incoming = new();
    readonly DispatcherTimer _flush = new() { Interval = TimeSpan.FromMilliseconds(150) };
    Process? _logcat;
    Device? _logcatDevice;
    StreamWriter? _logcatFile;
    readonly object _fileGate = new();
    readonly DispatcherTimerLite _logcatFilterDebounce;
    Brush? _err, _warn, _info, _dim;

    public ConsolePage()
    {
        InitializeComponent();
        ActivityList.ItemsSource = Fleet.Log.Entries;
        LogcatList.ItemsSource = _logcatView;
        _filterDebounce = new DispatcherTimerLite(TimeSpan.FromMilliseconds(200), ApplyActivityFilter);
        _logcatFilterDebounce = new DispatcherTimerLite(TimeSpan.FromMilliseconds(250), RebuildLogcat);
        _flush.Tick += (_, _) => FlushLogcat();
        Fleet.Log.Added += OnLogAdded;
        Loaded += (_, _) => ScrollActivityToEnd();
    }

    void ModeBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var logcat = (sender.SelectedItem?.Tag as string) == "logcat";
        ActivityBar.Visibility = logcat ? Visibility.Collapsed : Visibility.Visible;
        ActivityList.Visibility = logcat ? Visibility.Collapsed : Visibility.Visible;
        LogcatBar.Visibility = logcat ? Visibility.Visible : Visibility.Collapsed;
        LogcatList.Visibility = logcat ? Visibility.Visible : Visibility.Collapsed;
        CommandRow.Visibility = logcat ? Visibility.Collapsed : Visibility.Visible;
        LogcatEmpty.Visibility = logcat && _logcatView.Count == 0 && _logcat == null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= Activity =================

    void OnLogAdded(LogEntry e)
    {
        if (ActivityFilter.Text.Length > 0) return;
        if (_scrollPending) return;
        _scrollPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _scrollPending = false;
            ScrollActivityToEnd();
        });
    }

    void ScrollActivityToEnd()
    {
        if (ActivityList.Items.Count > 0) ActivityList.ScrollIntoView(ActivityList.Items[^1]);
    }

    void ActivityFilter_TextChanged(object sender, TextChangedEventArgs e) => _filterDebounce.Kick();

    void ApplyActivityFilter()
    {
        var q = ActivityFilter.Text.Trim();
        ActivityList.ItemsSource = q.Length == 0
            ? Fleet.Log.Entries
            : Fleet.Log.Entries.Where(x => x.Text.Contains(q, StringComparison.OrdinalIgnoreCase) || x.Source.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        ScrollActivityToEnd();
    }

    void CopyActivity_Click(object sender, RoutedEventArgs e)
    {
        var items = ActivityList.SelectedItems.Count > 0
            ? ActivityList.SelectedItems.Cast<LogEntry>()
            : (ActivityList.ItemsSource as IEnumerable<LogEntry>) ?? [];
        var p = new DataPackage();
        p.SetText(string.Join(Environment.NewLine, items.Select(i => i.ToString())));
        Clipboard.SetContent(p);
    }

    void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(Fleet.DataDir, "logs");
        Directory.CreateDirectory(dir);
        Dialogs.OpenFolder(dir);
    }

    void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        Fleet.Log.Entries.Clear();
        ApplyActivityFilter();
    }

    // ---- command line ----

    void CommandBox_GotFocus(object sender, RoutedEventArgs e) => CommandBox.ItemsSource = Fleet.Settings.Value.QuickCommands;

    void CommandBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = Fleet.Settings.Value.QuickCommands.Where(c => c.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    async void CommandBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string s) { sender.Text = s; return; }
        await RunCommand();
    }

    async void Run_Click(object sender, RoutedEventArgs e) => await RunCommand();

    async Task RunCommand()
    {
        var text = CommandBox.Text.Trim();
        if (text.Length == 0) return;
        var args = CommandLine.Split(text);
        if (args.Count > 0 && args[0].Equals("adb", StringComparison.OrdinalIgnoreCase)) args.RemoveAt(0);
        if (args.Count > 0 && args[0] == "-s")
        {
            Fleet.Log.Error(null, "Leave out -s. Commands run on the devices shown at the top of the page.");
            return;
        }
        if (args.Count > 0 && args[0] is "logcat" && !args.Contains("-d"))
        {
            Fleet.Log.Error(null, "Use the Logcat tab to stream logs, or add -d to print the current log once.");
            return;
        }
        if (args.Count > 0 && args[0] == "shell" && args.Count == 1)
        {
            Fleet.Log.Error(null, "Interactive shells aren't supported here. Type the command to run, for example: shell ls /sdcard");
            return;
        }
        CommandBox.Text = "";
        if (CommandLine.IsGlobal(args))
        {
            await Fleet.ExecWithTimeout(null, 60_000, args.ToArray());
            return;
        }
        var targets = Fleet.Devices.Targets;
        if (targets.Count == 0)
        {
            Fleet.Log.Error(null, "No device is ready. Connect or select a device first.");
            return;
        }
        await Task.WhenAll(targets.Select(d => Task.Run(() => Fleet.ExecWithTimeout(d, 300_000, args.ToArray()))));
    }

    void QuickMenu_Opening(object? sender, object e)
    {
        QuickMenu.Items.Clear();
        var cmds = Fleet.Settings.Value.QuickCommands;
        foreach (var c in cmds)
        {
            var item = new MenuFlyoutItem { Text = c };
            item.Click += (_, _) => { CommandBox.Text = c; CommandBox.Focus(FocusState.Programmatic); };
            QuickMenu.Items.Add(item);
        }
        QuickMenu.Items.Add(new MenuFlyoutSeparator());
        var save = new MenuFlyoutItem { Text = "Save current command", Icon = new SymbolIcon(Symbol.Add), IsEnabled = CommandBox.Text.Trim().Length > 0 };
        save.Click += (_, _) =>
        {
            var t = CommandBox.Text.Trim();
            if (t.StartsWith("adb ", StringComparison.OrdinalIgnoreCase)) t = t[4..];
            if (!cmds.Contains(t)) { cmds.Add(t); Fleet.Settings.Save(); }
        };
        QuickMenu.Items.Add(save);
        if (cmds.Count > 0)
        {
            var del = new MenuFlyoutSubItem { Text = "Remove", Icon = new SymbolIcon(Symbol.Delete) };
            foreach (var c in cmds)
            {
                var item = new MenuFlyoutItem { Text = c };
                item.Click += (_, _) => { cmds.Remove(c); Fleet.Settings.Save(); };
                del.Items.Add(item);
            }
            QuickMenu.Items.Add(del);
        }
    }

    // ================= Logcat =================

    void LogcatStart_Click(object sender, RoutedEventArgs e)
    {
        if (_logcat != null) { StopLogcat(); return; }
        var d = Fleet.Devices.Focused;
        if (d is not { IsOnline: true })
        {
            Fleet.Log.Error(null, "Select a ready device to stream its log.");
            return;
        }
        var level = (LogcatLevel.SelectedItem as ComboBoxItem)?.Tag as string ?? "V";
        _logcatDevice = d;
        if (LogcatToFile.IsChecked == true)
        {
            var path = Path.Combine(Fleet.Settings.DownloadsDir, $"logcat_{d.SafeName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt");
            _logcatFile = new StreamWriter(path) { AutoFlush = false };
            Fleet.Log.Add(d, $"Saving logcat to {path}");
        }
        try
        {
            _logcat = Fleet.Adb.StartStream(d.Serial, ["logcat", "-v", "time", $"*:{level}"], line =>
            {
                _incoming.Enqueue(line);
                if (_logcatFile != null) lock (_fileGate) _logcatFile?.WriteLine(line);
            }, code => DispatcherQueue.TryEnqueue(() =>
            {
                if (_logcat != null) Fleet.Log.Add(d, $"Logcat stopped (exit code {code})");
                StopLogcat();
            }));
        }
        catch (Exception ex)
        {
            Fleet.Log.Error(d, ex.Message);
            StopLogcat();
            return;
        }
        Fleet.Log.Add(d, "Logcat started");
        LogcatStart.Content = $"Stop ({d.DisplayName})";
        LogcatLevel.IsEnabled = LogcatToFile.IsEnabled = false;
        LogcatEmpty.Visibility = Visibility.Collapsed;
        _flush.Start();
    }

    void StopLogcat()
    {
        var p = _logcat;
        _logcat = null;
        if (p != null) AdbClient.TryKill(p);
        FlushLogcat();
        _flush.Stop();
        lock (_fileGate) { _logcatFile?.Flush(); _logcatFile?.Dispose(); _logcatFile = null; }
        LogcatStart.Content = "Start";
        LogcatLevel.IsEnabled = LogcatToFile.IsEnabled = true;
    }

    void FlushLogcat()
    {
        if (_incoming.IsEmpty) return;
        var q = LogcatFilter.Text.Trim();
        var batch = new List<string>();
        while (batch.Count < 3000 && _incoming.TryDequeue(out var l)) batch.Add(l);
        _logcatRaw.AddRange(batch);
        if (_logcatRaw.Count > LogcatCap * 2) _logcatRaw.RemoveRange(0, _logcatRaw.Count - LogcatCap);
        foreach (var l in batch)
            if (q.Length == 0 || l.Contains(q, StringComparison.OrdinalIgnoreCase)) _logcatView.Add(Make(l));
        var excess = _logcatView.Count - LogcatCap;
        for (var i = 0; i < excess; i++) _logcatView.RemoveAt(0);
        if (_logcatView.Count > 0) LogcatList.ScrollIntoView(_logcatView[^1]);
        lock (_fileGate) _logcatFile?.Flush();
    }

    LogcatLine Make(string l)
    {
        var res = Application.Current.Resources;
        _err ??= (Brush)res["SystemFillColorCriticalBrush"];
        _warn ??= (Brush)res["SystemFillColorCautionBrush"];
        _info ??= (Brush)res["TextFillColorPrimaryBrush"];
        _dim ??= (Brush)res["TextFillColorSecondaryBrush"];
        // "-v time" lines look like: 11-08 10:00:00.000 E/Tag( 123): message
        var lvl = l.Length > 19 ? l[19] : ' ';
        var brush = lvl switch { 'E' or 'F' or 'A' => _err, 'W' => _warn, 'I' => _info, _ => _dim };
        return new LogcatLine { Text = l, Brush = brush };
    }

    void LogcatFilter_TextChanged(object sender, TextChangedEventArgs e) => _logcatFilterDebounce.Kick();

    void RebuildLogcat()
    {
        var q = LogcatFilter.Text.Trim();
        _logcatView.Clear();
        foreach (var l in _logcatRaw.Where(l => q.Length == 0 || l.Contains(q, StringComparison.OrdinalIgnoreCase)).TakeLast(LogcatCap))
            _logcatView.Add(Make(l));
        if (_logcatView.Count > 0) LogcatList.ScrollIntoView(_logcatView[^1]);
    }

    void LogcatClear_Click(object sender, RoutedEventArgs e)
    {
        _logcatRaw.Clear();
        _logcatView.Clear();
        if (_logcatDevice is { IsOnline: true } d && _logcat == null)
            _ = Fleet.Adb.RunAsync(d.Serial, ["logcat", "-c"]);
    }
}
