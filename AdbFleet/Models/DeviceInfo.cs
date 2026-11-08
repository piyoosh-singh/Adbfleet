namespace AdbFleet.Models;

/// <summary>
/// A snapshot of one device's details. Immutable: a refresh replaces the whole object, so
/// bindings to <c>Info.X</c> update together.
/// </summary>
public sealed record DeviceInfo
{
    public string Build { get; init; } = "";
    public string Cpu { get; init; } = "";
    public string SecurityPatch { get; init; } = "";   // yyyy-mm-dd
    public string HardwareSerial { get; init; } = "";
    public string Codename { get; init; } = "";
    public string Screen { get; init; } = "";          // physical WxH
    public string ScreenOverride { get; init; } = "";
    public string Density { get; init; } = "";
    public string DensityOverride { get; init; } = "";
    public long MemTotalKb { get; init; }
    public long MemAvailKb { get; init; }
    public long StorageTotalKb { get; init; }
    public long StorageFreeKb { get; init; }
    public int Battery { get; init; } = -1;            // -1: no battery (TVs, boxes)
    public bool Charging { get; init; }
    public TimeSpan Uptime { get; init; }
    public DateTime ReadAt { get; init; } = DateTime.Now;

    static string Gb(long kb) => kb >= 1048576 ? $"{kb / 1048576.0:0.0} GB" : $"{kb / 1024.0:0} MB";

    // ---- Text for display ----

    public string StorageText => StorageTotalKb > 0 ? $"{Gb(StorageFreeKb)} free of {Gb(StorageTotalKb)}" : "Unknown";
    public string StorageShort => StorageTotalKb > 0 ? $"{Gb(StorageFreeKb)} / {Gb(StorageTotalKb)}" : "–";
    public double StorageUsedPercent => StorageTotalKb > 0 ? 100.0 * (StorageTotalKb - StorageFreeKb) / StorageTotalKb : 0;

    public string MemoryText => MemTotalKb > 0 ? $"{Gb(MemAvailKb)} free of {Gb(MemTotalKb)}" : "Unknown";
    public string MemoryShort => MemTotalKb > 0 ? $"{Gb(MemAvailKb)} / {Gb(MemTotalKb)}" : "–";
    public double MemoryUsedPercent => MemTotalKb > 0 ? 100.0 * (MemTotalKb - MemAvailKb) / MemTotalKb : 0;

    public string BatteryText => Battery < 0 ? "No battery" : $"{Battery}%{(Charging ? ", charging" : "")}";
    public string BatteryShort => Battery < 0 ? "–" : $"{Battery}%";

    public string ScreenText => Screen.Length == 0 ? "Unknown"
        : ScreenOverride.Length > 0 ? $"{ScreenOverride} (changed from {Screen})" : Screen;
    public string DensityText => Density.Length == 0 ? "Unknown"
        : DensityOverride.Length > 0 ? $"{DensityOverride} dpi (changed from {Density})" : $"{Density} dpi";
    public string ScreenShort => (ScreenOverride.Length > 0 ? ScreenOverride + "*" : Screen) is { Length: > 0 } s ? s : "–";

    public string UptimeText => Uptime == TimeSpan.Zero ? "Unknown"
        : Uptime.TotalDays >= 1 ? $"{(int)Uptime.TotalDays} d {Uptime.Hours} h" : $"{Uptime.Hours} h {Uptime.Minutes} min";

    public string ReadAtText => $"Read at {ReadAt:HH:mm}";

    public string PatchText => SecurityPatch.Length == 0 ? "Unknown"
        : PatchAgeMonths is int m && m >= 12 ? $"{SecurityPatch} ({m / 12} yr {m % 12} mo old)" : SecurityPatch;
    public string PatchShort => SecurityPatch.Length >= 7 ? SecurityPatch[..7] : SecurityPatch.Length > 0 ? SecurityPatch : "–";

    public int? PatchAgeMonths
    {
        get
        {
            if (!DateTime.TryParse(SecurityPatch, out var p)) return null;
            var now = DateTime.Today;
            return Math.Max(0, (now.Year - p.Year) * 12 + now.Month - p.Month);
        }
    }

    // ---- Warnings: values worth a second look ----

    public bool StorageLow => StorageTotalKb > 0 && (StorageFreeKb < 1048576 || StorageFreeKb < StorageTotalKb / 10);
    public bool MemoryLow => MemTotalKb > 0 && MemAvailKb < MemTotalKb / 10;
    public bool PatchOld => PatchAgeMonths >= 24;
    public bool BatteryLow => Battery is >= 0 and < 15 && !Charging;

}
