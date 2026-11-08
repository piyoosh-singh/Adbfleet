using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace AdbFleet.Models;

public sealed class AppInfo
{
    public required string Package { get; init; }
    public string ApkPath { get; init; } = "";
    public bool IsSystem { get; init; }
    public bool IsEnabled { get; init; } = true;
    public bool IsInstalled { get; init; } = true;

    public string KindText => IsSystem ? "System" : "User";
    public string StateText => !IsInstalled ? "Removed" : !IsEnabled ? "Disabled" : "";
    public Visibility StateVisibility => StateText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public double Dim => IsInstalled && IsEnabled ? 1.0 : 0.62;
}

public enum LogKind { Info, Command, Output, Success, Error }

public sealed class LogEntry
{
    public required DateTime Time { get; init; }
    public required string Source { get; init; }
    public required Brush SourceBrush { get; init; }
    public required string Text { get; init; }
    public required LogKind Kind { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");
    public FontWeight Weight => Kind == LogKind.Command ? new FontWeight { Weight = 600 } : new FontWeight { Weight = 400 };
    public string Prefix => Kind == LogKind.Command ? "adb " : "";

    public Brush TextBrush => (Brush)Application.Current.Resources[Kind switch
    {
        LogKind.Error => "SystemFillColorCriticalBrush",
        LogKind.Success => "SystemFillColorSuccessBrush",
        LogKind.Output => "TextFillColorSecondaryBrush",
        _ => "TextFillColorPrimaryBrush",
    }];

    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss} [{Source}] {Prefix}{Text}";
}

public sealed class ProcessRow
{
    public int Pid { get; init; }
    public string User { get; init; } = "";
    public long RssKb { get; init; }
    public string Name { get; init; } = "";
    public string MemText => RssKb >= 1024 ? $"{RssKb / 1024.0:0.0} MB" : $"{RssKb} KB";
    public bool IsApp => User.StartsWith("u0_", StringComparison.Ordinal) || User.StartsWith("u1", StringComparison.Ordinal) || Name.Contains('.');
}

public sealed class QueuedFile
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public long Size { get; init; }
    public string SizeText => Size >= 1 << 20 ? $"{Size / 1048576.0:0.0} MB" : $"{Size / 1024.0:0} KB";
    public string KindText => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
    {
        ".apk" => "APK",
        ".apks" => "Split APK bundle",
        ".xapk" => "XAPK bundle",
        ".apkm" => "APKM bundle",
        ".zip" => "ZIP bundle",
        _ => "File",
    };
    public bool IsBundle => System.IO.Path.GetExtension(Path).ToLowerInvariant() is ".apks" or ".xapk" or ".apkm" or ".zip";
}

public sealed class Fact
{
    public Fact(string label, string value) { Label = label; Value = value; }
    public string Label { get; }
    public string Value { get; }
}

public sealed class Preset
{
    public string Name { get; set; } = "";
    public List<string> Packages { get; set; } = new();
    public override string ToString() => Name;
}
