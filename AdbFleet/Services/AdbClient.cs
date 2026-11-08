using System.Diagnostics;
using System.Text;

namespace AdbFleet.Services;

public sealed record AdbResult(int ExitCode, string StdOut, string StdErr)
{
    public string Text
    {
        get
        {
            var o = StdOut.Trim();
            var e = StdErr.Trim();
            return o.Length == 0 ? e : e.Length == 0 ? o : o + "\n" + e;
        }
    }

    /// <summary>
    /// adb shell on older devices always exits 0, so the output is checked too.
    /// </summary>
    public bool Ok
    {
        get
        {
            if (ExitCode != 0) return false;
            var t = Text;
            return !(t.Contains("Failure", StringComparison.Ordinal)
                  || t.StartsWith("Error", StringComparison.OrdinalIgnoreCase)
                  || t.Contains("adb: error", StringComparison.OrdinalIgnoreCase)
                  || t.Contains("Exception occurred", StringComparison.Ordinal)
                  || t.Contains("Unknown package", StringComparison.Ordinal)
                  || t.Contains("SecurityException", StringComparison.Ordinal)
                  || t.Contains("not installed for", StringComparison.Ordinal));
        }
    }

    public string FirstLine => Text.Split('\n', 2)[0].Trim();
}

/// <summary>
/// Thin wrapper around adb.exe. Every device-scoped call passes "-s serial", so any number
/// of devices can be driven at the same time; the adb server multiplexes them.
/// </summary>
public sealed class AdbClient
{
    public string? AdbPath => ToolLocator.FindAdb();

    ProcessStartInfo Make(string exe, string? serial, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(serial))
        {
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(serial);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    string RequireAdb() => AdbPath ?? throw new InvalidOperationException(
        "adb.exe was not found. Open Settings and download platform-tools or choose adb.exe.");

    public async Task<AdbResult> RunAsync(string? serial, IReadOnlyList<string> args,
        CancellationToken ct = default, int timeoutMs = 120_000)
    {
        using var p = new Process { StartInfo = Make(RequireAdb(), serial, args) };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (se) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(p);
            ct.ThrowIfCancellationRequested();
            lock (so) lock (se)
                return new AdbResult(-1, so.ToString(), se + $"Timed out after {timeoutMs / 1000}s");
        }
        lock (so) lock (se)
            return new AdbResult(p.ExitCode, so.ToString().TrimEnd(), se.ToString().TrimEnd());
    }

    public Task<AdbResult> ShellAsync(string serial, string command, CancellationToken ct = default, int timeoutMs = 60_000)
        => RunAsync(serial, ["shell", command], ct, timeoutMs);

    /// <summary>Streams binary stdout (e.g. exec-out screencap) into a file.</summary>
    public async Task<long> RunToFileAsync(string serial, IReadOnlyList<string> args, string filePath, CancellationToken ct = default)
    {
        var psi = Make(RequireAdb(), serial, args);
        psi.StandardOutputEncoding = null;
        using var p = new Process { StartInfo = psi };
        p.Start();
        _ = p.StandardError.ReadToEndAsync(ct);
        await using (var fs = File.Create(filePath))
            await p.StandardOutput.BaseStream.CopyToAsync(fs, ct);
        await p.WaitForExitAsync(ct);
        return new FileInfo(filePath).Length;
    }

    /// <summary>Starts a long-running adb process (logcat) and reports each line.</summary>
    public Process StartStream(string serial, IReadOnlyList<string> args, Action<string> onLine, Action<int>? onExit = null)
    {
        var p = new Process { StartInfo = Make(RequireAdb(), serial, args), EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.Exited += (_, _) => onExit?.Invoke(SafeExit(p));
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    static int SafeExit(Process p) { try { return p.ExitCode; } catch { return -1; } }

    public static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    public static string Display(IEnumerable<string> args)
        => string.Join(' ', args.Select(a => a.Length == 0 || a.Contains(' ') ? $"\"{a}\"" : a));
}
