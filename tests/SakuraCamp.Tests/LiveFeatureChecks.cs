using System.Diagnostics;
using System.Text;
using SakuraCamp.Core;

static class LiveFeatureChecks
{
    public static async Task RunAsync(Action<bool, string> check, LaunchPlan plan, string backupRoot)
    {
        var id = Guid.NewGuid().ToString("N");
        string Frame(string data) => "[00:01:23]: " + ServerTelemetry.Marker(id) + data;
        check(ServerTelemetry.TryParse(Frame("{\"players\":{},\"day\":1,\"season\":\"winter\",\"paused\":true}"), id, "Caves", out var empty) && empty!.Players.Count == 0 && empty.Paused, "Lua empty player table is a successful zero-player sample");
        check(!ServerTelemetry.TryParse(Frame("{\"players\":{},\"day\":1,\"season\":\"winter\",\"paused\":true}"), Guid.NewGuid().ToString("N"), "Master", out _), "ignore delayed telemetry responses from a previous request");
        check(!ServerTelemetry.TryParse(ServerTelemetry.Command(id), id, "Master", out _), "console command echo cannot be mistaken for player data");
        foreach (var data in new[] { "{broken", "{\"error\":\"query unavailable\"}", "{\"players\":42}", "{\"players\":{},\"day\":0,\"season\":\"winter\",\"paused\":false}" })
            check(!ServerTelemetry.TryParse(Frame(data), id, "Master", out _), "invalid or unavailable status is not reported as an empty server: " + data);
        check(ServerTelemetry.TryParse(Frame("{\"players\":[{\"userid\":\"KU_test\",\"name\":\"中文🌸\",\"character\":\"wendy\",\"ping\":-1}],\"day\":2,\"season\":\"spring\",\"paused\":false}"), id, "Caves", out var unknownPing) && unknownPing!.Players.Single().PingMilliseconds is null, "unavailable game ping remains unknown instead of zero");
        var vitalFrame = Frame("""{"players":[{"userid":"KU_vitals","name":"角色","character":"wendy","netscore":1,"inworld":true,"health":{"current":0,"max":150},"hunger":{"current":75.5,"max":150},"sanity":{"current":90,"max":120}}],"day":12,"season":"autumn","paused":false}""");
        check(ServerTelemetry.TryParse(vitalFrame, id, "Master", out var vitals) && vitals!.Players.Single() is { Health.Current: 0, Hunger.Current: 75.5, Sanity.Maximum: 120, NetworkScore: 1, PingText: "网络一般" }, "real vital values include zero health, fractional hunger and official network quality");
        check(ServerTelemetry.TryParse(Frame("""{"players":[{"userid":"KU_unknown","netscore":null,"health":{"current":12,"max":0},"hunger":null}],"day":1,"season":"autumn","paused":false}"""), id, "Caves", out var absent) && absent!.Players.Single() is { Health: null, Hunger: null, Sanity: null, PingMilliseconds: null, NetworkScore: null }, "unavailable stats and zero maxima remain unknown instead of invented full bars");
        check(new VitalStatus(300, 150).Percent == 100 && new VitalStatus(-10, 150).Percent == 0, "modded vital values are preserved but bars stay within bounds");
        var creationKey = Guid.NewGuid().ToString("N");
        var tunnels = new[]
        {
            new TunnelInfo(10, "master", 216, "udp", "127.0.0.1", 41001, "41001", false, 0) { NodeHost = "test.example.com", Note = $"SakuraCamp:{creationKey}:Master" },
            new TunnelInfo(11, "caves", 216, "udp", "127.0.0.1", 41002, "41002", false, 0) { NodeHost = "test.example.com", Note = $"SakuraCamp:{creationKey}:Caves" }
        };
        var legacyManual = new WorldProfile { MasterTunnelId = 10, CavesTunnelId = 11, AutomaticTunnels = false };
        var pair = SakuraClient.SelectWorldTunnels(tunnels, legacyManual, true);
        check(pair.Master.Id == 10 && pair.Caves?.Id == 11, "regression: legacy AutomaticTunnels=false still selects both tunnels for one-click launch");
        check(SakuraClient.SelectWorldTunnels(tunnels, new() { CreationKey = creationKey, AutomaticTunnels = false }, true).Caves?.Id == 11,
            "recover both world-owned tunnels by creation marker if IDs are missing");
        check(SakuraClient.SelectWorldTunnels(tunnels, new() { MasterAddress = "test.example.com:41001", CavesAddress = "test.example.com:41002" }, true).Master.Id == 10,
            "legacy address-only scheme resolves exact tunnels without manual activation");
        check(SakuraClient.SelectWorldTunnels(tunnels, legacyManual, false).Caves is null, "surface-only world starts only its surface tunnel");
        void Reject(Action action, string title)
        {
            try { action(); } catch (InvalidOperationException) { check(true, title); return; }
            throw new Exception("FAIL: " + title);
        }
        Reject(() => SakuraClient.SelectWorldTunnels(tunnels[..1], legacyManual, true), "missing cave tunnel blocks launch instead of starting only the game");
        Reject(() => SakuraClient.SelectWorldTunnels(tunnels, new() { MasterTunnelId = 999, CavesTunnelId = 11, CreationKey = creationKey }, true), "deleted saved tunnel cannot silently select a different account tunnel");
        Reject(() => SakuraClient.SelectWorldTunnels(new[] { tunnels[0], tunnels[1] with { Node = 999 } }, legacyManual, true), "mixed nodes block automatic launch");
        Reject(() => SakuraClient.SelectWorldTunnels(new[] { tunnels[0], tunnels[1] with { Status = 1 } }, legacyManual, true), "disabled tunnel blocks automatic launch");
        var missingFrpcRejected = false;
        try { _ = new ManagedSession().StartWithTunnelsAsync(plan, backupRoot, null!); }
        catch (ArgumentNullException) { missingFrpcRejected = true; }
        check(missingFrpcRejected, "product startup entry point requires tunnel process and cannot fall back to world-only startup");
        check(FrpcInstaller.ReadyProxyName("2026/10/29 10:50:05 [I] [216/889394/e0b7] [account.master] 隧道启动成功") == "account.master",
            "official Chinese frpc success identifies the proxy name instead of the shared node/run ID");
        check(FrpcInstaller.ReadyProxyName("2026/10/29 10:50:05 [I] [216/889394/e0b7] [account.caves] 隧道启动成功") == "account.caves",
            "two official Chinese tunnel logs retain distinct proxy identities");
        check(FrpcInstaller.ReadyProxyName("UDP 隧道启动成功") is null, "generic Chinese frpc banner is not counted as an additional tunnel");
        var settingsStore = new SettingsStore(Path.Combine(backupRoot, "mode-settings"));
        var saved = new AppSettings(); saved.Profiles["world"] = legacyManual;
        settingsStore.Save(saved);
        check(SakuraClient.SelectWorldTunnels(tunnels, settingsStore.Load().Profiles["world"], true).Caves?.Id == 11, "restarted app recovers both tunnels even from a persisted manual scheme");
        if (Process.GetProcessesByName("dontstarve_dedicated_server_nullrenderer_x64").Length > 0) return;

        ProcessStartInfo Shell(string script)
        {
            var start = WorldFiles.HiddenProcess(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script))); return start;
        }
        var timer = Stopwatch.StartNew();
        var session = new ManagedSession();
        var firstWorldAt = TimeSpan.Zero;
        var onlineVerified = false;
        await session.StartWithTunnelsAsync(plan, backupRoot,
            frpc: Shell("$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8; Write-Output 'UDP 隧道启动成功'; Write-Output '[I] [216/shared-run] [account.master] 隧道启动成功'; Write-Output '[I] [216/shared-run] [account.master] 隧道启动成功'; Start-Sleep -Milliseconds 800; Write-Output '[I] [216/shared-run] [account.caves] 隧道启动成功'; while ($null -ne [Console]::ReadLine()) {}"),
            factory: shard =>
            {
                if (!onlineVerified) throw new Exception("A world must not launch before official online verification");
                if (firstWorldAt == TimeSpan.Zero) firstWorldAt = timer.Elapsed;
                return Shell("Write-Output '" + (shard == "Master" ? "Server registered" : "secondary shard LUA is now ready!") + "'; while ($null -ne ($line=[Console]::ReadLine())) { if ($line -eq 'c_shutdown(true)') { exit 0 } }");
            }, timeout: TimeSpan.FromSeconds(15), verifyOnline: () => { onlineVerified = true; return Task.CompletedTask; });
        try
        {
            check(firstWorldAt.TotalMilliseconds >= 800 && session.IsReady, "automatic startup waits for two distinct tunnel proxies before launching worlds");
            check(onlineVerified, "product startup verifies official online state before launching either shard");
            var timeoutStatus = await session.ReadStatusAsync(TimeSpan.FromMilliseconds(150));
            check(timeoutStatus.Shards.Count == 2 && timeoutStatus.Shards.All(s => s.Sample is null && s.Detail.Contains("暂未收到")), "telemetry timeout remains unknown and does not invent player counts");
        }
        finally { await session.StopAsync(TimeSpan.FromSeconds(5)); }
        check(!session.HasProcesses, "saving worlds also stops the tool-owned tunnel process");
        var incomplete = new ManagedSession(); bool launched = false; bool timedOut = false;
        try
        {
            await incomplete.StartWithTunnelsAsync(plan, backupRoot,
                frpc: Shell("Write-Output '[master-proxy] start proxy success'; Write-Output '[master-proxy] start proxy success'; while ($null -ne [Console]::ReadLine()) {}"),
                factory: _ => { launched = true; throw new Exception("Must not launch a world"); }, timeout: TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { timedOut = true; }
        check(timedOut && !launched && !incomplete.HasProcesses, "repeated success for one tunnel cannot hide a missing cave tunnel");
        var rejected = new ManagedSession(); bool worldStarted = false; bool verificationRejected = false;
        try
        {
            await rejected.StartWithTunnelsAsync(plan, backupRoot,
                frpc: Shell("Write-Output '[master-proxy] start proxy success'; Write-Output '[caves-proxy] start proxy success'; while ($null -ne [Console]::ReadLine()) {}"),
                factory: _ => { worldStarted = true; throw new Exception("Must not start"); },
                verifyOnline: () => throw new InvalidOperationException("fixture: officially offline"));
        }
        catch (InvalidOperationException) { verificationRejected = true; }
        check(verificationRejected && !worldStarted && !rejected.HasProcesses, "failed official verification stops owned frpc and never launches the worlds");
    }
}
