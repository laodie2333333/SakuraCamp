using System.Diagnostics;
using System.Text;
using SakuraCamp.Core;

static class LauncherChecks
{
    private sealed class Service : ILauncherTunnelService
    {
        public readonly Dictionary<int, LauncherTunnel> Rows = [];
        public readonly List<(int Id, bool Enabled)> Changes = [];
        public int FailAfterEnabling;
        public bool FailNextStop;
        public Task<IReadOnlyList<LauncherTunnel>> ReadTunnelsAsync(IReadOnlyList<int> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LauncherTunnel>>(ids.Select(id => Rows[id]).ToArray());
        public Task SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
        {
            Changes.Add((id, enabled));
            if (!enabled && FailNextStop) { FailNextStop = false; throw new IOException("fixture stop unavailable"); }
            Rows[id] = Rows[id] with { Enabled = enabled, State = enabled ? 2 : 0 };
            if (enabled && id == FailAfterEnabling) throw new IOException("fixture response lost after mutation");
            return Task.CompletedTask;
        }
    }

    public static async Task RunAsync(Action<bool, string> check, LaunchPlan plan, string backupRoot)
    {
        var selected = new[]
        {
            new TunnelInfo(10, "master", 216, "udp", "127.0.0.1", 41001, "41001", false, 0),
            new TunnelInfo(11, "caves", 216, "udp", "127.0.0.1", 41002, "41002", false, 0)
        };
        Service Fixture()
        {
            var service = new Service();
            foreach (var t in selected) service.Rows[t.Id] = new(t.Id, t.Node, t.Name, t.Type, t.Status, false, 0, t.LocalIp, t.LocalPort, t.Remote);
            return service;
        }
        var normal = Fixture(); var lease = new LauncherTunnelLease(normal, selected);
        await lease.StartAsync();
        check(normal.Rows.Values.All(t => t.Enabled && t.State == 2), "official launcher switches for both tunnels become enabled and running");
        await lease.StopAsync();
        check(normal.Rows.Values.All(t => !t.Enabled), "stop disables both tool-owned official launcher switches");

        var shared = Fixture(); shared.Rows[10] = shared.Rows[10] with { Enabled = true, State = 2 };
        var sharedLease = new LauncherTunnelLease(shared, selected);
        await sharedLease.StartAsync(); await sharedLease.StopAsync();
        check(shared.Rows[10].Enabled && !shared.Rows[11].Enabled && shared.Changes.All(c => c.Id != 10), "previously running official tunnels are reused and preserved on stop");

        var mismatch = Fixture(); mismatch.Rows[11] = mismatch.Rows[11] with { LocalPort = 12345 };
        try { await new LauncherTunnelLease(mismatch, selected).StartAsync(); throw new Exception("expected mismatch rejection"); }
        catch (InvalidOperationException) { check(mismatch.Changes.Count == 0, "wrong launcher account/config is rejected before any switch is changed"); }

        var failed = Fixture(); failed.FailAfterEnabling = 11;
        var failedLease = new LauncherTunnelLease(failed, selected);
        try { await failedLease.StartAsync(); throw new Exception("expected lost response"); }
        catch (IOException) { await failedLease.StopAsync(); }
        check(failed.Rows.Values.All(t => !t.Enabled), "lost response after enabling cave rolls back both official switches");

        var retry = Fixture(); var retryLease = new LauncherTunnelLease(retry, selected);
        await retryLease.StartAsync(); retry.FailNextStop = true;
        try { await retryLease.StopAsync(); throw new Exception("expected stop failure"); }
        catch (InvalidOperationException) { }
        check(retryLease.EnabledByUs.SequenceEqual(new[] { 10 }), "failed disable retains ownership for a safe stop retry");
        await retryLease.StopAsync();
        check(retry.Rows.Values.All(t => !t.Enabled) && retryLease.EnabledByUs.Count == 0, "retry closes only the remaining tool-owned switch");

        if (Process.GetProcessesByName("dontstarve_dedicated_server_nullrenderer_x64").Length > 0) return;
        ProcessStartInfo Shell(string shard)
        {
            var start = WorldFiles.HiddenProcess(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"));
            var script = "Write-Output ('fixture-pid:'+$PID); Write-Output '" + (shard == "Master" ? "Server registered" : "secondary shard LUA is now ready!") + "'; while ($null -ne ($line=[Console]::ReadLine())) { if ($line -eq 'c_shutdown(true)') { exit 0 } }";
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script))); return start;
        }
        var lifecycle = Fixture(); var lifecycleLease = new LauncherTunnelLease(lifecycle, selected);
        var session = new ManagedSession(); var verified = false; var worlds = new System.Collections.Concurrent.ConcurrentBag<int>();
        session.Log += line => { var m = System.Text.RegularExpressions.Regex.Match(line, @"fixture-pid:(\d+)"); if (m.Success) worlds.Add(int.Parse(m.Groups[1].Value)); };
        await session.StartWithLauncherAsync(plan, backupRoot, () => lifecycleLease.StartAsync(), async () =>
        {
            check(worlds.Count == 2 && worlds.All(id => { try { using var p = Process.GetProcessById(id); return p.HasExited; } catch (ArgumentException) { return true; } }), "worlds exit before official tunnel switches are disabled");
            await lifecycleLease.StopAsync();
        }, () => { verified = lifecycle.Rows.Values.All(t => t.Enabled && t.State == 2); return Task.CompletedTask; }, factory: shard =>
        {
            if (!verified) throw new Exception("world before launcher verification"); return Shell(shard);
        });
        check(verified && session.IsReady, "official service startup verifies both switches before either world starts");
        await session.StopAsync();
        check(!session.HasProcesses && lifecycle.Rows.Values.All(t => !t.Enabled), "managed save/stop releases official service ownership");

        var abort = Fixture(); abort.FailAfterEnabling = 11; var abortLease = new LauncherTunnelLease(abort, selected);
        var aborted = new ManagedSession(); var worldStarted = false;
        try
        {
            await aborted.StartWithLauncherAsync(plan, backupRoot, () => abortLease.StartAsync(), abortLease.StopAsync, () => Task.CompletedTask,
                factory: _ => { worldStarted = true; throw new Exception("must never launch"); });
        }
        catch (IOException) { }
        check(!worldStarted && !aborted.HasProcesses && abort.Rows.Values.All(t => !t.Enabled), "managed launcher failure rolls back switches and blocks world startup");
    }
}
