using AdbFleet.Models;

namespace AdbFleet.Services;

public sealed record BatchSummary(int Ok, int Failed)
{
    public int Total => Ok + Failed;
}

/// <summary>Runs one piece of work on many devices in parallel and marks each device's result.</summary>
public static class Batch
{
    /// <param name="work">Returns null on success or a short failure message.</param>
    public static async Task<BatchSummary> Run(string action, IReadOnlyList<Device> targets, Func<Device, Task<string?>> work)
    {
        if (targets.Count == 0)
        {
            Fleet.Log.Error(null, $"{action}: no device selected. Check one or more devices on the left.");
            return new(0, 0);
        }

        await Fleet.OnUI(() =>
        {
            foreach (var d in targets) { d.Run = RunState.Busy; d.RunMessage = action; }
        });

        var tasks = targets.Select(async d =>
        {
            string? err;
            try { err = await Task.Run(() => work(d)); }
            catch (Exception ex) { err = ex.Message; }
            await Fleet.OnUI(() =>
            {
                d.Run = err == null ? RunState.Ok : RunState.Failed;
                d.RunMessage = err == null ? $"{action}: done" : $"{action}: {err}";
            });
            if (err == null) Fleet.Log.Success(d, $"{action}: done");
            else Fleet.Log.Error(d, $"{action} failed: {err}");
            return err == null;
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var ok = results.Count(x => x);
        if (targets.Count > 1)
            Fleet.Log.Add(null, $"{action}: {ok} of {targets.Count} devices succeeded", ok == targets.Count ? LogKind.Success : LogKind.Error);
        return new(ok, targets.Count - ok);
    }

    /// <summary>Runs per-item work (e.g. per package) on each device; a device fails if any item fails.</summary>
    public static Task<BatchSummary> RunItems<T>(string action, IReadOnlyList<Device> targets, IReadOnlyList<T> items,
        Func<Device, T, Task<string?>> work)
        => Run(action, targets, async d =>
        {
            var failures = new List<string>();
            foreach (var item in items)
            {
                var e = await work(d, item);
                if (e != null) failures.Add(e);
            }
            return failures.Count == 0 ? null
                 : failures.Count == 1 ? failures[0]
                 : $"{failures.Count} of {items.Count} failed. First: {failures[0]}";
        });
}

public static class CommandLine
{
    /// <summary>Splits a command line respecting double quotes.</summary>
    public static List<string> Split(string input)
    {
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQ = false, any = false;
        foreach (var c in input)
        {
            if (c == '"') { inQ = !inQ; any = true; continue; }
            if (char.IsWhiteSpace(c) && !inQ)
            {
                if (cur.Length > 0 || any) { args.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0 || any) args.Add(cur.ToString());
        return args;
    }

    /// <summary>adb commands that talk to the server rather than to one device.</summary>
    public static bool IsGlobal(IReadOnlyList<string> args) => args.Count > 0 && args[0] is
        "devices" or "connect" or "disconnect" or "pair" or "version" or "kill-server" or "start-server"
        or "mdns" or "help" or "host-features" or "reconnect";
}
