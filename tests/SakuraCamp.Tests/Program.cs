using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SakuraCamp.Core;

var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "work", "tests-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(workspace);
var passed = 0;
void Check(bool condition, string title) { if (!condition) throw new Exception("FAIL: " + title); passed++; Console.WriteLine("PASS: " + title); }
void Throws(Action action, string title) { try { action(); } catch (InvalidOperationException) { Check(true, title); return; } throw new Exception("FAIL (did not reject): " + title); }

var ini = new IniDocument("[NETWORK]\r\nserver_port = 10999\r\n\r\n[OTHER]\r\nserver_port = 42\r\n");
ini.Set("NETWORK", "server_port", "44812");
Check(ini.Get("NETWORK", "server_port") == "44812" && ini.Get("OTHER", "server_port") == "42", "CRLF port edit only affects target section");
Check(ini.ToString().Contains("\r\n\r\n[OTHER]"), "preserves blank lines and line endings");
ini.Set("NETWORK", "cluster_password", "a=b"); Check(ini.Get("NETWORK", "cluster_password") == "a=b", "password with equals sign survives");
Throws(() => new IniDocument("[NETWORK]\nserver_port=1\nserver_port=2").Set("NETWORK", "server_port", "3"), "reject duplicate keys");
Throws(() => new IniDocument("[NETWORK]\na=1\n[NETWORK]\nb=2").Get("NETWORK", "a"), "reject duplicate sections");
Throws(() => ini.Set("NETWORK", "cluster_password", "a\nb"), "reject newline injection");
Check(Endpoint.Parse(" Frp-Example.com:44812 ") == new Endpoint("frp-example.com", 44812), "parse pasted endpoint");
foreach (var input in new[] { "host:0", "host:65536", "a\"b:42", "https://host:123", "host:abc" }) Throws(() => Endpoint.Parse(input), "reject invalid endpoint " + input);
var command = WorldFiles.Connect("host", 44812, "a\"b\\c"); Check(command == "c_connect(\"host\", 44812, \"a\\\"b\\\\c\")", "escape password for Lua connection command");
var redacted = LogReader.Redact("token = aaa\npds-g^example-secret\npassword = abc\nreal-secret", "real-secret");
Check(!redacted.Contains("aaa") && !redacted.Contains("example-secret") && !redacted.Contains("real-secret"), "redact credentials from logs");
Check(LogReader.Diagnose("E_MODS_DOWNLOAD_ERROR workshop-123").Any(s => s.Contains("客户端下载")), "identify client mod download failure");

var root = Path.Combine(workspace, "Klei", "DoNotStarveTogether");
var cluster = Path.Combine(root, "123456", "Cluster_5"); Directory.CreateDirectory(cluster);
File.WriteAllText(Path.Combine(cluster, "cluster.ini"), "[NETWORK]\r\ncluster_name = 测试世界\r\ncluster_password = test123\r\noffline_cluster = false\r\n[GAMEPLAY]\r\nmax_players = 6\r\n[SHARD]\r\nshard_enabled = true\r\n");
File.WriteAllText(Path.Combine(cluster, "cluster_token.txt"), "pds-test-aaaaaaaaaaaaaaaaaaaaaaaaaaaa");
foreach (var shard in new[] { "Master", "Caves" }) { Directory.CreateDirectory(Path.Combine(cluster, shard, "save")); File.WriteAllText(Path.Combine(cluster, shard, "server.ini"), "[NETWORK]\r\nserver_port = 10999\r\n[SHARD]\r\nid = " + shard + "\r\n"); File.WriteAllText(Path.Combine(cluster, shard, "modoverrides.lua"), "return { [\"workshop-123\"]={ enabled=true }, [\"workshop-456\"]={enabled=true} }"); }
File.WriteAllText(Path.Combine(cluster, "Master", "save", "world.dat"), "precious world save");
var world = Discovery.ReadWorld(cluster); Check(Discovery.Worlds(root).Count == 1 && world.HasCaves && world.HasPassword, "discover account-based world with caves and password");
var server = Path.Combine(workspace, "game", "bin64", "dontstarve_dedicated_server_nullrenderer_x64.exe"); Directory.CreateDirectory(Path.GetDirectoryName(server)!); File.WriteAllText(server, "fixture");
var workshop = Path.Combine(workspace, "workshop"); Directory.CreateDirectory(Path.Combine(workshop, "content", "322330", "123")); File.WriteAllText(Path.Combine(workshop, "content", "322330", "123", "modinfo.lua"), "name = \"Test Mod\"\nversion = \"1.0\"");
var mods = Discovery.Mods(world, workshop, server); Check(mods.Count == 2 && mods.Single(m => m.Id == "123").Downloaded && !mods.Single(m => m.Id == "456").Downloaded, "mod discovery, deduplication and missing file detection");
int FreePort() { using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)udp.Client.LocalEndPoint!).Port; }
var port1 = FreePort(); var port2 = FreePort(); while (port2 == port1) port2 = FreePort();
var plan = new LaunchPlan(world, server, workshop, new Endpoint("frp-example.com", port1), new Endpoint("frp-example.com", port2));
plan.Validate(); Check(true, "validate complete launch plan");
Throws(() => (plan with { Caves = new Endpoint("other-node.com", port2) }).Validate(), "reject mixed node hosts");
Throws(() => (plan with { Caves = new Endpoint("frp-example.com", port1) }).Validate(), "reject duplicate world ports");
var info = WorldFiles.ServerStart(plan, "Master"); Check(info.ArgumentList.Contains("123456") && info.ArgumentList.Contains(root.Split(Path.DirectorySeparatorChar)[^1]) && !info.ArgumentList.Any(a => a.StartsWith("pds-")), "launch arguments preserve account layout without credentials");
var backupRoot = Path.Combine(workspace, "backups"); var backup = WorldFiles.Backup(cluster, backupRoot); Check(File.ReadAllText(Path.Combine(backup, "Master", "save", "world.dat")) == "precious world save", "full save backup");
Throws(() => WorldFiles.Backup(cluster, Path.Combine(cluster, "backup")), "reject recursive backup destination");
using var tunnelJson = JsonDocument.Parse("{\"id\":12,\"name\":\"Master\",\"node\":3,\"type\":\"udp\",\"local_ip\":\"127.0.0.1\",\"local_port\":44812,\"remote\":\"44812\",\"online\":false,\"status\":0}");
var tunnel = SakuraClient.ParseTunnel(tunnelJson.RootElement); Check(tunnel.GetEndpoint("frp-example.com").Port == 44812, "parse official Sakura API tunnel schema");
Throws(() => (tunnel with { LocalPort = 10999 }).GetEndpoint("frp-example.com"), "reject local-remote port mismatch for caves");
var store = new SettingsStore(Path.Combine(workspace, "settings")); var settings = new AppSettings { SaveRoot = root }; settings.Profiles[cluster] = new() { MasterAddress = "example.com:44812" }; store.Save(settings); Check(store.Load().Profiles[cluster].MasterAddress == "example.com:44812", "persist per-world profile without credentials");

ProcessStartInfo Fake(string shard, bool fail = false, bool slow = false)
{
    var payload = JsonSerializer.Serialize(new { players = new[] { new { userid = "KU_fixture", name = "玩家 🌸", character = "wilson", ping = 42, inworld = true } }, day = 12, season = "autumn", paused = false });
    var encodedPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
    var script = fail ? "exit 7" : "$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8; Write-Output '" + (shard == "Master" ? "Server registered" : "secondary shard LUA is now ready!") + "'; while ($null -ne ($line = [Console]::ReadLine())) { if ($line -match '\\[SakuraCamp.Status:([a-f0-9]{32})\\]') { Write-Output ($Matches[0] + [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedPayload + "'))) }; if ($line -eq 'c_shutdown(true)') { " + (slow ? "Start-Sleep -Milliseconds 350; " : "") + "Write-Output 'Saved'; exit 0 } }";
    var start = WorldFiles.HiddenProcess(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script))); return start;
}
if (Process.GetProcessesByName("dontstarve_dedicated_server_nullrenderer_x64").Length == 0)
{
    var session = new ManagedSession(); var messages = new List<string>(); session.Log += s => { lock (messages) messages.Add(s); };
    await session.StartAsync(plan, backupRoot, factory: shard => Fake(shard), timeout: TimeSpan.FromSeconds(15));
    Check(session.HasProcesses, "two-shard startup waits for actual readiness signals");
    var status = await session.ReadStatusAsync(TimeSpan.FromSeconds(2));
    Check(status.Shards.Count == 2 && status.Shards.All(s => s.Sample?.Day == 12 && s.Sample.Players.Single().Name == "玩家 🌸" && s.Sample.Players.Single().PingMilliseconds == 42), "live console query returns Unicode players, world day and game ping for both shards");
    Check(status.Shards.All(s => s.MemoryBytes > 0), "live status samples owned server process memory");
    Check(!messages.Any(m => m.Contains("SakuraCamp.Status:") || m.Contains("KU_fixture")), "telemetry does not leak player IDs into diagnostic logs");
    Check(IniDocument.Load(Path.Combine(cluster, "Master", "server.ini")).Get("NETWORK", "server_port") == port1.ToString(), "backup precedes actual port configuration");
    Check(await session.StopAsync(TimeSpan.FromSeconds(5)) && !session.HasProcesses, "save and shutdown owned processes gracefully");
    Check((await session.ReadStatusAsync()).Shards.Count == 0, "stopped session cannot return stale players");
    var failure = new ManagedSession(); bool failed = false;
    try { await failure.StartAsync(plan, backupRoot, factory: shard => Fake(shard, shard == "Caves"), timeout: TimeSpan.FromSeconds(10)); } catch (InvalidOperationException) { failed = true; }
    Check(failed && !failure.HasProcesses, "partial startup failure saves and closes remaining shard");
    var slowSession = new ManagedSession();
    await slowSession.StartAsync(plan, backupRoot, factory: shard => Fake(shard, slow: true), timeout: TimeSpan.FromSeconds(15));
    Check(!await slowSession.StopAsync(TimeSpan.FromMilliseconds(1)) && slowSession.HasProcesses, "slow save timeout never force-kills game processes");
    await Task.Delay(650); Check(await slowSession.StopAsync(TimeSpan.FromSeconds(3)), "can finish shutdown after slow save");
    var tunnelFailure = new ManagedSession(); var serverStarted = false; string tunnelError = "";
    try
    {
        await tunnelFailure.StartAsync(plan, backupRoot, frpc: Fake("tunnel", fail: true), factory: shard => { serverStarted = true; return Fake(shard); }, timeout: TimeSpan.FromSeconds(10));
    }
    catch (InvalidOperationException ex) { tunnelError = ex.Message; }
    Check(!serverStarted && !tunnelFailure.HasProcesses && tunnelError.Contains("7"), "failed tunnel does not launch worlds and reports exit code");
}
else Console.WriteLine("SKIP lifecycle tests: real DST server is active; refusing to touch it.");
await LiveFeatureChecks.RunAsync(Check, plan, backupRoot);
await LauncherChecks.RunAsync(Check, plan, backupRoot);
await TunnelOnlineChecks.RunAsync(Check);
ModManagerChecks.Run(Check, workspace);
var apiRequests = new List<string>();
using var fakeHttp = new HttpClient(new FixtureHandler(request =>
{
    Check(request.Headers.Authorization?.ToString() == "Bearer fixture-access-key", "Sakura API uses authorization header");
    var path = request.RequestUri!.AbsolutePath; apiRequests.Add(path);
    return path.EndsWith("/nodes", StringComparison.Ordinal)
        ? "{\"216\":{\"name\":\"Fixture node\",\"host\":\"frp-example.com\"}}"
        : "[{\"id\":1,\"name\":\"Master\",\"node\":216,\"type\":\"udp\",\"local_ip\":\"127.0.0.1\",\"local_port\":44812,\"remote\":\"44812\",\"status\":0},{\"id\":2,\"name\":\"Caves\",\"node\":216,\"type\":\"udp\",\"local_ip\":\"127.0.0.1\",\"local_port\":26445,\"remote\":26445,\"status\":0},{\"id\":3,\"name\":\"Other node\",\"node\":7,\"type\":\"udp\",\"local_ip\":\"127.0.0.1\",\"local_port\":12345,\"remote\":\"12345\",\"status\":0}]";
}));
var resolved = await new SakuraClient(fakeHttp).TunnelsAsync("fixture-access-key");
Check(apiRequests.SequenceEqual(new[] { "/v4/tunnels", "/v4/nodes" }), "numeric tunnel remote resolves node host through official nodes API");
Check(resolved[0].GetEndpoint() == new Endpoint("frp-example.com", 44812) && resolved[0].ConnectionAddress == "frp-example.com:44812", "master address auto-generated without user host input");
Check(resolved[1].GetEndpoint() == new Endpoint("frp-example.com", 26445), "caves address auto-generated for numeric JSON port");
Throws(() => resolved[2].GetEndpoint(), "missing node host produces clear error instead of guessing address");
var directRequests = 0;
using var directHttp = new HttpClient(new FixtureHandler(_ => { directRequests++; return "[{\"id\":4,\"type\":\"udp\",\"local_ip\":\"127.0.0.1\",\"local_port\":12345,\"remote\":\"direct.example.com:12345\",\"status\":0}]"; }));
var direct = await new SakuraClient(directHttp).TunnelsAsync("fixture-access-key");
Check(directRequests == 1 && direct[0].GetEndpoint().Host == "direct.example.com", "complete remote address remains supported without unnecessary node query");
using var failedHttp = new HttpClient(new FixtureHandler(request => request.RequestUri!.AbsolutePath.EndsWith("nodes", StringComparison.Ordinal) ? throw new HttpRequestException("fixture node failure") : "[{\"remote\":\"44812\"}]"));
var nodeFailure = false; try { await new SakuraClient(failedHttp).TunnelsAsync("fixture-access-key"); } catch (HttpRequestException) { nodeFailure = true; }
Check(nodeFailure, "node lookup failure is surfaced instead of exposing half-resolved list");
settings.Profiles[cluster].MasterTunnelId = 1; settings.Profiles[cluster].CavesTunnelId = 2; store.Save(settings);
Check(store.Load().Profiles[cluster].CavesTunnelId == 2, "remember selected tunnel ids for each world");
Check(WorldFiles.StorageRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Klei")) == "APP:Klei/", "use game-native storage alias for Chinese Windows profile path");
var standaloneDir = Path.Combine(workspace, "standalone"); Directory.CreateDirectory(standaloneDir); var frpcFixture = Path.Combine(standaloneDir, "frpc.exe"); File.WriteAllText(frpcFixture, "fixture");
var frpcInfo = SakuraClient.StartInfo(frpcFixture, "fixture-secret", new[] { 1, 2 });
Check(frpcInfo.Environment["NATFRP_TOKEN"] == "fixture-secret" && frpcInfo.Environment["NATFRP_TARGET"] == "1,2" && !frpcInfo.ArgumentList.Any(a => a.Contains("fixture-secret")), "pass official frpc credentials via environment instead of process arguments");
File.WriteAllText(Path.Combine(standaloneDir, "SakuraFrpService.exe"), "fixture");
Throws(() => SakuraClient.StartInfo(frpcFixture, "fixture-secret", new[] { 1, 2 }), "reject launcher companion before trying to start it");
var probePath = Environment.GetEnvironmentVariable("SAKURACAMP_TEST_FRPC");
if (!string.IsNullOrWhiteSpace(probePath)) { await FrpcInstaller.ProbeAsync(probePath); Check(true, "real official standalone frpc passes command-line compatibility probe"); }
var privateNode = new NodeInfo(216, "测试三线", "frp-example.com", 0, 4 | 32);
Check(privateNode.IsAvailable(false) && !privateNode.IsAvailable(true), "private UDP node is not automatically eligible for public servers");
Check((privateNode with { Flag = 4 | 32 | 2048 }).IsAvailable(true), "allow explicitly flagged public game node");
Check((privateNode with { Name = "测试多线" }).IsAvailable(true), "allow documented multiline public node name");
Check(!(privateNode with { Flag = 32 }).IsAvailable(false) && !(privateNode with { Flag = 4 | 32 | 512 }).IsAvailable(false), "exclude full or offline nodes");
using var creationHandler = new CreationFixture(); using var creationHttp = new HttpClient(creationHandler);
var creator = new SakuraClient(creationHttp); var creationKey = Guid.NewGuid().ToString("N"); var pending = new HashSet<string>(); var journalWrites = 0;
var pair = await creator.EnsureWorldTunnelsAsync("fixture-key", 216, false, creationKey, true, pendingShards: pending, persistPending: () => journalWrites++);
Check(creationHandler.Creates == 2 && creationHandler.Edits == 2 && pair.Master.LocalPort == pair.Master.GetEndpoint().Port && pair.Caves!.LocalPort == pair.Caves.GetEndpoint().Port, "create same-node UDP pair and align assigned local and remote ports");
Check(creationHandler.CreatedForms.All(f => f["type"] == "udp" && f["local_ip"] == "127.0.0.1" && !f.ContainsKey("remote")), "request random remote ports instead of hardcoded occupied ports");
Check(pending.Count == 0 && journalWrites == 4, "journal pending mutations before submitting requests and clear after readback");
var again = await creator.EnsureWorldTunnelsAsync("fixture-key", 216, false, creationKey, true);
Check(creationHandler.Creates == 2 && creationHandler.Edits == 2 && pair.Master.Id == again.Master.Id, "repeated create reuses owned tunnels without duplicates");
bool publicBlocked = false; try { await creator.EnsureWorldTunnelsAsync("fixture-key", 216, true, Guid.NewGuid().ToString("N"), true); } catch (InvalidOperationException) { publicBlocked = true; }
Check(publicBlocked && creationHandler.Creates == 2, "public scope policy checked before remote mutations");
using var partialHandler = new CreationFixture { FailCavesOnce = true }; using var partialHttp = new HttpClient(partialHandler);
var partialCreator = new SakuraClient(partialHttp); var partialKey = Guid.NewGuid().ToString("N"); var partialPending = new HashSet<string>(); string partialError = "";
try { await partialCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, partialKey, true, pendingShards: partialPending); } catch (InvalidOperationException e) { partialError = e.Message; }
Check(partialHandler.Creates == 1 && partialError.Contains("fixture tunnel quota") && partialPending.Count == 0, "retain master and explain quota error if cave creation is rejected");
var recovered = await partialCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, partialKey, true, pendingShards: partialPending);
Check(partialHandler.Creates == 2 && recovered.Master.Id == 101 && recovered.Caves!.Id == 102, "retry partial creation only fills missing cave tunnel");
using var lostHandler = new CreationFixture { LoseMasterResponseOnce = true }; using var lostHttp = new HttpClient(lostHandler);
var lostCreator = new SakuraClient(lostHttp); var lostKey = Guid.NewGuid().ToString("N"); var lostPending = new HashSet<string>();
try { await lostCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, lostKey, false, pendingShards: lostPending); } catch (InvalidOperationException) { }
Check(lostPending.Contains("Master") && lostHandler.Creates == 1, "lost response retains pending marker");
lostHandler.HideTunnels = true;
try { await lostCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, lostKey, false, pendingShards: lostPending); } catch (InvalidOperationException) { }
Check(lostHandler.Creates == 1, "ambiguous outcome plus stale list never submits duplicate create");
lostHandler.HideTunnels = false;
var single = await lostCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, lostKey, false, pendingShards: lostPending);
Check(lostHandler.Creates == 1 && single.Caves is null && lostPending.Count == 0, "recover lost response by owner marker and support master-only world");
creationHandler.Tunnels[0]["note"] = "someone else's tunnel"; bool collision = false;
try { await creator.EnsureWorldTunnelsAsync("fixture-key", 216, false, creationKey, true); } catch (InvalidOperationException) { collision = true; }
Check(collision && creationHandler.Edits == 2 && creationHandler.Creates == 2, "refuse to edit an unrelated tunnel with colliding name");
settings.Profiles[cluster].CreationKey = creationKey; settings.Profiles[cluster].PendingTunnelShards.Add("Caves"); store.Save(settings);
Check(store.Load().Profiles[cluster].CreationKey == creationKey && store.Load().Profiles[cluster].PendingTunnelShards.Contains("Caves"), "pending creation journal survives application restart");
using var absentHandler = new CreationFixture { FailBeforeCreateOnce = true }; using var absentHttp = new HttpClient(absentHandler);
var absentCreator = new SakuraClient(absentHttp);
var absentProfile = new WorldProfile { CreationKey = Guid.NewGuid().ToString("N"), CreationNodeId = 216 };
try { await absentCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, absentProfile.CreationKey, true, pendingShards: absentProfile.PendingTunnelShards); } catch (InvalidOperationException) { }
Check(absentHandler.Creates == 0 && absentProfile.PendingTunnelShards.Contains("Master"), "network failure without creation reproduces stuck pending state");
var recoveryWrites = 0;
var absentCheck = await absentCreator.RecoverCreationAsync("fixture-key", absentProfile, false, () => recoveryWrites++);
Check(absentCheck.ConfirmationRequired && absentCheck.MissingNames.Count == 1 && absentProfile.PendingTunnelShards.Contains("Master") && absentProfile.CreationNodeId == 216 && recoveryWrites == 0,
    "empty API list alone does not clear uncertain request without user confirmation");
var absentRecovered = await absentCreator.RecoverCreationAsync("fixture-key", absentProfile, true, () => recoveryWrites++);
Check(absentRecovered.NodeUnlocked && absentProfile.CreationNodeId is null && absentProfile.PendingTunnelShards.Count == 0 && recoveryWrites == 1 && absentHandler.Creates == 0,
    "confirmed absence clears pending and unlocks node without remote mutations");
var afterReset = await absentCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, absentProfile.CreationKey, true, pendingShards: absentProfile.PendingTunnelShards);
Check(absentHandler.Creates == 2 && afterReset.Caves is not null, "create pair succeeds after recovering genuinely absent request");
var delayedProfile = new WorldProfile { CreationKey = absentProfile.CreationKey, CreationNodeId = 216, PendingTunnelShards = ["Master", "Caves"] };
absentHandler.HideTunnels = true;
Check((await absentCreator.RecoverCreationAsync("fixture-key", delayedProfile, false)).ConfirmationRequired, "stale list requires explicit absence confirmation");
absentHandler.HideTunnels = false;
var delayedRecovered = await absentCreator.RecoverCreationAsync("fixture-key", delayedProfile, true);
Check(delayedRecovered.FoundCount == 2 && !delayedRecovered.NodeUnlocked && delayedProfile.CreationNodeId == 216 && delayedProfile.PendingTunnelShards.Count == 0 && absentHandler.Creates == 2,
    "requery after confirmation discovers delayed tunnels and preserves node ownership");
var oneProfile = new WorldProfile { CreationKey = absentProfile.CreationKey, CreationNodeId = 216, PendingTunnelShards = ["Caves"] };
absentHandler.Tunnels.RemoveAll(t => ((string)t["name"]).EndsWith('c'));
var oneRecovered = await absentCreator.RecoverCreationAsync("fixture-key", oneProfile, true);
Check(oneRecovered.FoundCount == 1 && !oneRecovered.NodeUnlocked && oneProfile.CreationNodeId == 216 && absentHandler.Edits == 2,
    "recovery of absent cave preserves existing master and original node");
using var deniedHandler = new CreationFixture { NextCreateStatus = HttpStatusCode.InternalServerError };
using var deniedHttp = new HttpClient(deniedHandler); var deniedCreator = new SakuraClient(deniedHttp); var deniedPending = new HashSet<string>(); var deniedMessage = "";
try { await deniedCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, Guid.NewGuid().ToString("N"), true, pendingShards: deniedPending); } catch (InvalidOperationException e) { deniedMessage = e.Message; }
Check(deniedPending.Count == 0 && deniedHandler.Creates == 0 && deniedMessage.Contains("403") && deniedMessage.Contains("fixture denied") && !deniedMessage.Contains("fixture-key"),
    "official HTTP 500 with explicit business rejection preserves reason and clears pending with secrets redacted");
deniedHandler.NextCreateStatus = HttpStatusCode.InternalServerError; deniedHandler.NextCreateCode = 500;
try { await deniedCreator.EnsureWorldTunnelsAsync("fixture-key", 216, false, Guid.NewGuid().ToString("N"), false, pendingShards: deniedPending); } catch (InvalidOperationException) { }
Check(deniedPending.Contains("Master"), "generic server error retains uncertain request until verified recovery");
var emptyProfile = new WorldProfile { CreationKey = Guid.NewGuid().ToString("N"), CreationNodeId = 216 };
var emptyRecovery = await absentCreator.RecoverCreationAsync("fixture-key", emptyProfile, false);
Check(emptyRecovery.NodeUnlocked && !emptyRecovery.ConfirmationRequired && emptyProfile.CreationNodeId is null,
    "definitively failed creation without matching tunnels unlocks node after read-only check");
await NodeChangeChecks.Run(Check);
Console.WriteLine($"ALL {passed} CHECKS PASSED. Fixtures: {workspace}");

sealed class FixtureHandler(Func<HttpRequestMessage, string> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(request), Encoding.UTF8, "application/json") });
}
