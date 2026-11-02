using System.Diagnostics;
using System.Net;
using System.Text;
using SakuraCamp.Core;

internal static class ServerUpdateChecks
{
    public static async Task Run(string workspace, Action<bool, string> check)
    {
        var root = Path.Combine(workspace, "server-updates"); Directory.CreateDirectory(root);
        var worldPath = Path.Combine(root, "world");
        Directory.CreateDirectory(worldPath);
        File.WriteAllText(Path.Combine(worldPath, "cluster.ini"), "[NETWORK]\ncluster_name=fixture\noffline_cluster=false\n[SHARD]\nshard_enabled=true\n");
        foreach (var shard in new[] { "Master", "Caves" })
        {
            var save = Path.Combine(worldPath, shard, "save");
            Directory.CreateDirectory(Path.Combine(save, "session", "ABCDEF1234567890"));
            File.WriteAllText(Path.Combine(save, "shardindex"), "return {session_id=\"ABCDEF1234567890\"}");
            File.WriteAllText(Path.Combine(worldPath, shard, "server.ini"), "[NETWORK]\nserver_port=10999\n");
            Snapshot(shard, 100);
        }
        var world = Discovery.ReadWorld(worldPath);
        void Snapshot(string shard, long version, string name = "0000000002") => File.WriteAllText(Path.Combine(worldPath, shard, "save", "session", "ABCDEF1234567890", name),
            "local savedata={}\ntablefunctions[\"meta_fn\"] = function()\nreturn {build_version=\"" + version + "\",saveversion=5.27}\nend");
        void Reject(Action action, string title)
        {
            var rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, title);
        }
        async Task RejectAsync(Func<Task> action, string title)
        {
            var rejected = false; try { await action(); } catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { rejected = true; }
            check(rejected, title);
        }

        var oldRoot = Path.Combine(root, "old-server"); var managed = Path.Combine(root, "managed-server");
        var client = Install(Path.Combine(root, "library", "steamapps", "common", "Don't Starve Together"), 200, "322330");
        var old = Install(oldRoot, 100);
        var latest = Install(Path.Combine(root, "latest"), 200);
        var inspection = ServerVersions.Inspect(world, old, [client]);
        check(inspection.MinimumVersion == 100 && inspection.Shards.All(s => s.GeneratedVersion == 100), "read ground and cave world generation versions without executing Lua");
        Reject(() => ServerVersions.RequireCompatible(inspection), "reject outdated server even when it matches old world generation version");
        ServerVersions.RequireCompatible(ServerVersions.Inspect(world, latest, [client]));
        check(true, "new server may load an older world without downgrading server or editing save");

        Snapshot("Caves", 250);
        var future = ServerVersions.Inspect(world, latest, [client]);
        check(future.MinimumVersion == 250, "cave world version cannot be hidden by a compatible ground world");
        Reject(() => ServerVersions.RequireCompatible(future), "block world newer than server");
        Snapshot("Caves", 100);
        Snapshot("Master", 999, "0000000001");
        check(ServerVersions.ReadShard(worldPath, "Master").GeneratedVersion == 100, "inspect selected latest snapshot rather than older rollback files");
        var session = Path.Combine(worldPath, "Master", "save", "session", "ABCDEF1234567890");
        Directory.CreateDirectory(Path.Combine(session, "PLAYER"));
        File.WriteAllText(Path.Combine(session, "PLAYER", "0000009999"), "meta={build_version=999}");
        check(ServerVersions.ReadShard(worldPath, "Master").GeneratedVersion == 100, "ignore player snapshots when checking world version");
        var other = Path.Combine(worldPath, "Master", "save", "session", "1111111111111111"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "0000009999"), "meta={build_version=999}");
        check(ServerVersions.ReadShard(worldPath, "Master").GeneratedVersion == 100, "respect shardindex session identity over unrelated sessions");
        File.WriteAllText(Path.Combine(session, "0000000002"), new string('x', 32760) + "\ntablefunctions[\"meta_fn\"] = function() return {build_version=\"100\"} end");
        check(ServerVersions.ReadShard(worldPath, "Master").GeneratedVersion == 100, "version parser handles metadata crossing read buffer boundary");
        File.WriteAllText(Path.Combine(session, "0000000002"), "KLEI     1 local savedata={} tablefunctions[\"meta_fn\"] = function() return {build_version=\"100\"} end");
        check(ServerVersions.ReadShard(worldPath, "Master").GeneratedVersion == 100, "uncompressed KLEI wrapper does not hide version");
        File.WriteAllText(Path.Combine(session, "0000000002"), "return {unknown={build_version=999}}");
        Reject(() => ServerVersions.RequireReadable(ServerVersions.Inspect(world, latest, [client])), "unknown or missing metadata blocks startup instead of assuming compatibility");
        Snapshot("Master", 100);

        var backend = new Backend { Version = 200 };
        var updater = new ServerUpdateService(backend); var logs = new List<string>();
        var originalBytes = File.ReadAllBytes(old);
        var updated = await updater.EnsureAsync(world, old, [client], managed, false, logs.Add);
        check(backend.Updates == 1 && updated.Server.Root == managed && updated.Server.Version == 200, "outdated server is automatically replaced by verified managed server selection");
        check(File.ReadAllBytes(old).SequenceEqual(originalBytes) && ServerVersions.Installation(old).Version == 100, "update never overwrites third party server deployment");
        backend.Current = true;
        await updater.EnsureAsync(world, updated.Server.Executable, [client], managed, false, logs.Add);
        check(backend.Updates == 1, "current verified server is reused without redownloading");
        await updater.EnsureAsync(world, updated.Server.Executable, [client], managed, true, logs.Add);
        check(backend.Updates == 2, "explicit repair performs file validation even when current");
        check(logs.Any(l => l.Contains("地上生成版本")) && logs.Any(l => l.Contains("更新后")), "version checks log installed, world and updated versions");

        var blank = await updater.EnsureAsync(world, "", [client], managed, false, logs.Add);
        check(blank.Server.Version == 200, "missing server can be installed automatically without launching a world");
        backend.FailUpdate = true;
        await RejectAsync(() => updater.EnsureAsync(world, old, [client], managed, true, logs.Add), "update failure propagates and cannot authorize startup");
        backend.FailUpdate = false; backend.Version = 90;
        await RejectAsync(() => updater.EnsureAsync(world, old, [client], managed, true, logs.Add), "nominal update success is rejected if actual installed version is too old");
        backend.Version = 200; backend.Current = false;
        await RejectAsync(() => updater.EnsureAsync(world, old, [client], managed, true, logs.Add), "post update Steam verification failure blocks startup");
        backend.Current = true;
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await RejectAsync(() => updater.EnsureAsync(world, old, [client], managed, true, logs.Add, cancel.Token), "cancellation prevents update from being accepted");
        Install(Path.GetDirectoryName(Path.GetDirectoryName(client)!)!, 190, "322330");
        Reject(() => ServerVersions.RequireCompatible(ServerVersions.Inspect(world, latest, [client])), "outdated game client is reported rather than downgrading server");
        Install(Path.GetDirectoryName(Path.GetDirectoryName(client)!)!, 200, "322330", "updatebeta");
        Reject(() => ServerVersions.RequireReadable(ServerVersions.Inspect(world, latest, [client])), "test branch cannot be silently replaced with public release");
        Install(Path.GetDirectoryName(Path.GetDirectoryName(client)!)!, 200, "322330");
        var gameUpdate = new ServerUpdateService(new Backend { Version = 200 });
        var migrated = await gameUpdate.EnsureAsync(world, client, [client], managed, true, logs.Add);
        check(migrated.Server.AppId == "343050" && ServerVersions.Installation(client).AppId == "322330", "repair of bundled game server installs isolated dedicated server and preserves game identity");

        var handler = new ApiHandler("{\"response\":{\"success\":true,\"up_to_date\":true}}");
        using var http = new HttpClient(handler);
        var steam = new SteamServerUpdater(http, Path.Combine(root, "steamcmd"));
        check(await steam.IsCurrentAsync(ServerVersions.Installation(client), default) && handler.LastUri!.Query.Contains("appid=322330") && handler.LastUri.Query.Contains("version=123"), "Steam API compares game app build identity rather than world generation version number");
        handler.Body = "{\"response\":{\"success\":true,\"up_to_date\":false}}";
        check(!await steam.IsCurrentAsync(ServerVersions.Installation(client), default), "Steam outdated response requests update");
        handler.Body = "{}";
        await RejectAsync(() => steam.IsCurrentAsync(ServerVersions.Installation(client), default), "malformed Steam metadata blocks unknown-version startup");
        handler.Fail = true;
        await RejectAsync(() => steam.IsCurrentAsync(ServerVersions.Installation(client), default), "offline Steam check fails explicitly");
        check(SteamServerUpdater.ParseDedicatedBuild("\"343050\" { \"branches\" { \"public\" { \"buildid\" \"26335402\" } \"updatebeta\" { \"buildid\" \"999\" } } }") == 26335402, "dedicated server uses authoritative public Steam depot build rather than unsupported listing API");
        Reject(() => SteamServerUpdater.ParseDedicatedBuild("\"322330\" { \"branches\" { \"public\" { \"buildid\" \"123\" } } }"), "dedicated metadata cannot be confused with game app metadata");
        Reject(() => SteamServerUpdater.ParseDedicatedBuild("\"343050\" { \"branches\" { \"updatebeta\" { \"buildid\" \"123\" } } }"), "beta build is never accepted as current public server build");
        var bootstrap = SteamServerUpdater.ReadBootstrapPackage("\"steamcmd_win32\" { \"file\" \"steamcmd_win32.zip." + new string('a', 40) + "\" \"size\" \"123456\" \"sha2\" \"" + new string('b', 64) + "\" }");
        check(bootstrap.Size == 123456 && bootstrap.Sha256.Length == 64, "current SteamCMD HTTPS bootstrap requires official package size and SHA256");
        Reject(() => SteamServerUpdater.ReadBootstrapPackage("\"steamcmd_win32\" { \"file\" \"../../evil.exe\" }"), "SteamCMD package manifest rejects path traversal and missing integrity metadata");
        check(SteamRuntimePaths.IsAscii("C:\\ProgramData\\SakuraCamp") && !SteamRuntimePaths.IsAscii("C:\\用户\\SteamCMD"), "SteamCMD English directory requirement detects Chinese Windows user paths");

        ProcessStartInfo Shell(string script)
        {
            var info = WorldFiles.HiddenProcess(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"));
            info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command"); info.ArgumentList.Add(script); return info;
        }
        await SteamServerUpdater.RunAsync(Shell("Write-Output \"Success! App '343050' fully installed.\"; exit 0"), logs.Add, TimeSpan.FromSeconds(15), default);
        check(true, "SteamCMD runner accepts successful download signal and exit code");
        await RejectAsync(() => SteamServerUpdater.RunAsync(Shell("exit 0"), logs.Add, TimeSpan.FromSeconds(15), default), "SteamCMD exit zero without success signal is not accepted");
        await RejectAsync(() => SteamServerUpdater.RunAsync(Shell("Write-Output 'ERROR! download failed'; exit 1"), logs.Add, TimeSpan.FromSeconds(15), default), "SteamCMD error and nonzero exit block startup");
        using var runCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await RejectAsync(() => SteamServerUpdater.RunAsync(Shell("Start-Sleep -Seconds 60"), logs.Add, TimeSpan.FromSeconds(10), runCancel.Token), "cancelled SteamCMD child is stopped before caller proceeds");
        var bootstrapMarker = Path.Combine(root, "bootstrap-marker").Replace("'", "''");
        await SteamServerUpdater.RunAsync(Shell("if (Test-Path -LiteralPath '" + bootstrapMarker + "') { Write-Output \"Success! App '343050' fully installed.\" } else { Set-Content -LiteralPath '" + bootstrapMarker + "' -Value done; Write-Output 'Update complete, launching...' }; exit 0"), logs.Add, TimeSpan.FromSeconds(15), default);
        check(logs.Any(l => l.Contains("自身更新完成")), "SteamCMD bootstrap relaunch resumes app update rather than treating bootstrap exit as install success");

        File.WriteAllText(Path.Combine(worldPath, "cluster_token.txt"), "pds-fixture-aaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        int FreePort() { using var socket = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
        var firstPort = FreePort(); var secondPort = FreePort(); while (firstPort == secondPort) secondPort = FreePort();
        var crashPlan = new LaunchPlan(Discovery.ReadWorld(worldPath), latest, root, new("example.com", firstPort), new("example.com", secondPort));
        var crash = new ManagedSession(); var fatalMessage = "";
        try
        {
            await crash.StartAsync(crashPlan, Path.Combine(root, "backups"), factory: shard => shard == "Caves"
                ? Shell("Write-Output '[string scripts/components/vaultroommanager.lua]:981: bad argument to SetPosition'; Write-Output 'LUA ERROR'; 1..25 | ForEach-Object { Write-Output 'worker thread termination' }; exit 6")
                : Shell("Write-Output 'Server registered'; while ($null -ne ($line=[Console]::ReadLine())) { if ($line -eq 'c_shutdown(true)') { exit 0 } }"), timeout: TimeSpan.FromSeconds(15));
        }
        catch (InvalidOperationException error) { fatalMessage = error.Message; }
        check(fatalMessage.Contains("vaultroommanager.lua") && fatalMessage.Contains("SetPosition") && !crash.HasProcesses,
            "crash dialog retains actual Lua cause after later thread messages and closes remaining shard safely");

        var diagnosticDir = Path.Combine(root, "logs"); var export = Path.Combine(root, "complete.txt");
        await using (var journal = new DiagnosticJournal(diagnosticDir))
        {
            journal.Append("Version: 123456");
            var longLine = new string('中', 100000); journal.Append(longLine);
            for (var i = 0; i < 15000; i++) journal.Append("line-" + i);
            journal.Append(LogReader.Redact("token=pds-test-secret\npassword=test-password"));
            journal.Append("late-credential");
            await journal.ExportAsync(export, "late-credential");
            var content = await File.ReadAllTextAsync(export);
            check(content.StartsWith("Version: 123456") && content.Contains("line-0" + Environment.NewLine) && content.Contains("line-14999") && content.Contains(longLine), "complete export retains startup, all 15000 messages and full oversized lines");
            check(!content.Contains("pds-test-secret") && !content.Contains("test-password") && !content.Contains("late-credential"), "complete export retains redaction including credentials supplied at export time");
            journal.Append("after-export");
            await journal.ExportAsync(export);
            check((await File.ReadAllTextAsync(export)).Contains("after-export"), "export flush barrier allows live logging to continue");
            await RejectAsync(() => journal.ExportAsync(journal.Path), "cannot overwrite live diagnostic journal during export");
        }
        var diskLog = Directory.GetFiles(diagnosticDir).Single();
        check((await File.ReadAllTextAsync(diskLog)).Contains("after-export"), "application close flushes all complete diagnostic output to disk");
    }

    private static string Install(string root, long version, string app = "343050", string branch = "public")
    {
        var executable = Path.Combine(root, "bin64", ServerVersions.ExecutableName); Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "fixture server"); File.WriteAllText(Path.Combine(root, "version.txt"), version.ToString());
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        File.WriteAllText(Path.Combine(root, "steamapps", $"appmanifest_{app}.acf"), $"\"AppState\" {{ \"appid\" \"{app}\" \"StateFlags\" \"4\" \"buildid\" \"123\" \"BetaKey\" \"{branch}\" }}");
        return executable;
    }
    private sealed class Backend : IServerUpdateBackend
    {
        public bool Current = true, FailUpdate; public long Version; public int Updates;
        public Task<bool> IsCurrentAsync(ServerInstallation installation, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Current); }
        public Task UpdateAsync(string directory, Action<string> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Updates++;
            if (FailUpdate) throw new InvalidOperationException("fixture download failure");
            Install(directory, Version); return Task.CompletedTask;
        }
    }
    private sealed class ApiHandler(string body) : HttpMessageHandler
    {
        public string Body = body; public bool Fail; public Uri? LastUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            if (Fail) throw new HttpRequestException("fixture offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }
}
