using System.Collections.ObjectModel;
using AdbFleet.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AdbFleet.Services;

public sealed class LogService
{
    const int Cap = 4000;
    public ObservableCollection<LogEntry> Entries { get; } = new();
    readonly object _fileGate = new();
    Brush? _appBrush;

    public event Action<LogEntry>? Added;

    public void Add(Device? d, string text, LogKind kind = LogKind.Info)
    {
        var now = DateTime.Now;
        var source = d?.DisplayName ?? "AdbFleet";
        void Push()
        {
            _appBrush ??= (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
            var e = new LogEntry { Time = now, Source = source, SourceBrush = d?.Brush ?? _appBrush, Text = text.TrimEnd(), Kind = kind };
            Entries.Add(e);
            while (Entries.Count > Cap) Entries.RemoveAt(0);
            Added?.Invoke(e);
            WriteFile(e);
        }
        if (Fleet.UI == null) return;
        if (Fleet.UI.HasThreadAccess) Push(); else Fleet.UI.TryEnqueue(Push);
    }

    public void Error(Device? d, string text) => Add(d, text, LogKind.Error);
    public void Success(Device? d, string text) => Add(d, text, LogKind.Success);

    void WriteFile(LogEntry e)
    {
        var line = e.ToString() + Environment.NewLine;
        Task.Run(() =>
        {
            try
            {
                var dir = Path.Combine(Fleet.DataDir, "logs");
                Directory.CreateDirectory(dir);
                lock (_fileGate) File.AppendAllText(Path.Combine(dir, $"{e.Time:yyyy-MM-dd}.log"), line);
            }
            catch { }
        });
    }
}
