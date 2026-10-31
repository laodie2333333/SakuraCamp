using System.Text.Json;
using SakuraCamp.Core;

static class NodeChangeChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        const string token = "fixture-key";
        async Task<(SakuraClient Api, WorldProfile Profile)> Seed(CreationFixture handler, HttpClient http)
        {
            var api = new SakuraClient(http); var profile = new WorldProfile { CreationKey = Guid.NewGuid().ToString("N"), CreationNodeId = 216 };
            var pair = await api.EnsureWorldTunnelsAsync(token, 216, false, profile.CreationKey, true);
            profile.MasterTunnelId = pair.Master.Id; profile.CavesTunnelId = pair.Caves!.Id;
            profile.MasterAddress = pair.Master.ConnectionAddress; profile.CavesAddress = pair.Caves.ConnectionAddress;
            return (api, profile);
        }
        async Task<bool> Reject(Func<Task> action) { try { await action(); return false; } catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { return true; } }
        using var handler = new CreationFixture(); using var http = new HttpClient(handler);
        var (api, profile) = await Seed(handler, http);
        var oldKey = profile.CreationKey; var oldIds = new[] { profile.MasterTunnelId, profile.CavesTunnelId };
        handler.Tunnels.Add(new() { ["id"] = 9000, ["node"] = 216, ["name"] = "unrelated", ["note"] = "", ["type"] = "udp", ["local_ip"] = "127.0.0.1", ["local_port"] = 45000, ["remote"] = "45000", ["online"] = false, ["status"] = 0 });
        var before = handler.Mutations.Count; var journal = ""; var persistedBeforeDelete = false;
        void Persist()
        {
            journal = JsonSerializer.Serialize(profile);
            if (profile.NodeChange is { OldDeleted: false, OldTunnels.Count: 2 } && handler.DeleteCalls == 0) persistedBeforeDelete = true;
        }
        var pair = await api.ChangeWorldNodeAsync(token, profile, 217, true, Persist);
        check(handler.Mutations[before].StartsWith("delete:") && handler.Creates == 4 && handler.DeleteCalls == 1, "node selection deletes old pair before creating new pair");
        check(pair.Master.Node == 217 && pair.Caves!.Node == 217 && pair.Master.LocalPort == pair.Master.GetEndpoint().Port && pair.Caves.LocalPort == pair.Caves.GetEndpoint().Port, "replacement pair binds new node and aligns both ports");
        check(handler.Tunnels.Any(t => (int)t["id"] == 9000) && !handler.Tunnels.Any(t => oldIds.Contains((int)t["id"])), "node replacement preserves unrelated account tunnels");
        check(persistedBeforeDelete && profile.NodeChange is null && profile.CreationKey != oldKey && profile.MasterTunnelId == pair.Master.Id && profile.SavedNode?.Id == 217, "replacement journal is persisted before deletion and bindings finalize after both tunnels");
        await api.ChangeWorldNodeAsync(token, profile, 217, true, Persist);
        check(handler.Creates == 4 && handler.DeleteCalls == 1, "selecting the already-bound node reuses completed tunnels");
        using var partial = new CreationFixture(); using var partialHttp = new HttpClient(partial);
        var (partialApi, partialProfile) = await Seed(partial, partialHttp);
        partial.PartialDeleteOnce = true;
        check(await Reject(() => partialApi.ChangeWorldNodeAsync(token, partialProfile, 217, true, () => { })), "partial deletion pauses replacement before new creation");
        check(partial.Creates == 2 && partialProfile.NodeChange is { OldDeleted: false }, "partial deletion retains journal and submits no replacement create");
        partialProfile = JsonSerializer.Deserialize<WorldProfile>(JsonSerializer.Serialize(partialProfile))!;
        await partialApi.ChangeWorldNodeAsync(token, partialProfile, 217, true, () => { });
        check(partial.Creates == 4 && partial.DeleteCalls == 2 && partialProfile.NodeChange is null, "restart resumes deleting only the remaining old tunnel");
        using var lost = new CreationFixture(); using var lostHttp = new HttpClient(lost);
        var (lostApi, lostProfile) = await Seed(lost, lostHttp); lost.LoseDeleteResponseOnce = true;
        check(await Reject(() => lostApi.ChangeWorldNodeAsync(token, lostProfile, 217, true, () => { })) && lost.Creates == 2, "lost delete response blocks new tunnels until readback on retry");
        await lostApi.ChangeWorldNodeAsync(token, lostProfile, 217, true, () => { });
        check(lost.DeleteCalls == 1 && lost.Creates == 4, "lost delete response resumes from absence without duplicate deletes or creates");
        using var failCreate = new CreationFixture(); using var failHttp = new HttpClient(failCreate);
        var (failApi, failProfile) = await Seed(failCreate, failHttp); failCreate.FailCavesOnce = true;
        check(await Reject(() => failApi.ChangeWorldNodeAsync(token, failProfile, 217, true, () => { })) && failCreate.Creates == 3 && failProfile.NodeChange is { OldDeleted: true }, "partial new-pair creation retains new master and incomplete replacement state");
        check(await Reject(() => failApi.ChangeWorldNodeAsync(token, failProfile, 216, true, () => { })) && failCreate.DeleteCalls == 1, "unfinished node change cannot silently switch to another target");
        failProfile = JsonSerializer.Deserialize<WorldProfile>(JsonSerializer.Serialize(failProfile))!;
        await failApi.ChangeWorldNodeAsync(token, failProfile, 217, true, () => { });
        check(failCreate.Creates == 4 && failCreate.DeleteCalls == 1 && failProfile.NodeChange is null, "retry partial replacement only creates the missing cave");
        using var bad = new CreationFixture(); using var badHttp = new HttpClient(bad);
        var (badApi, badProfile) = await Seed(bad, badHttp);
        bad.Flags = 32;
        check(await Reject(() => badApi.ChangeWorldNodeAsync(token, badProfile, 217, true, () => { })) && bad.DeleteCalls == 0, "unavailable target is rejected before old-tunnel deletion");
        bad.Flags = 4 | 32; bad.Tunnels[0]["note"] = "somebody-else";
        check(await Reject(() => badApi.ChangeWorldNodeAsync(token, badProfile, 217, true, () => { })) && bad.DeleteCalls == 0, "matching saved ID with wrong ownership cannot authorize deletion");
        using var first = new CreationFixture(); using var firstHttp = new HttpClient(first); var firstApi = new SakuraClient(firstHttp); var fresh = new WorldProfile();
        var firstPair = await firstApi.ChangeWorldNodeAsync(token, fresh, 217, true, () => { });
        check(first.Creates == 2 && first.DeleteCalls == 0 && fresh.MasterTunnelId == firstPair.Master.Id, "first node selection creates and binds a new pair immediately");
        using var singleHandler = new CreationFixture(); using var singleHttp = new HttpClient(singleHandler); var singleApi = new SakuraClient(singleHttp); var singleProfile = new WorldProfile();
        await singleApi.ChangeWorldNodeAsync(token, singleProfile, 217, false, () => { });
        check(singleHandler.Creates == 1 && singleProfile.CavesTunnelId is null, "master-only worlds retain single-tunnel topology");
        check(!journal.Contains(token), "replacement journal contains no access credentials");
        using var online = new CreationFixture(); using var onlineHttp = new HttpClient(online);
        var (onlineApi, onlineProfile) = await Seed(online, onlineHttp);
        online.Tunnels.ForEach(t => t["online"] = true);
        await onlineApi.ChangeWorldNodeAsync(token, onlineProfile, 217, true, () => { });
        check(online.DeleteCalls == 1 && online.Creates == 4, "official deletion disconnects old online tunnels without requiring manual client toggles");
        using var denied = new CreationFixture(); using var deniedHttp = new HttpClient(denied);
        var (deniedApi, deniedProfile) = await Seed(denied, deniedHttp); denied.RejectDelete = true;
        check(await Reject(() => deniedApi.ChangeWorldNodeAsync(token, deniedProfile, 217, true, () => { })) && denied.Creates == 2 && denied.Tunnels.Count == 2,
            "rejected deletion retains old pair and never creates replacement tunnels");
        using var kick = new CreationFixture(); using var kickHttp = new HttpClient(kick);
        var (kickApi, kickProfile) = await Seed(kick, kickHttp); kick.FailKickOnce = true;
        check(await Reject(() => kickApi.ChangeWorldNodeAsync(token, kickProfile, 217, true, () => { })) && kick.Creates == 2 && kickProfile.NodeChange is { OldDeleted: false },
            "official failure to disconnect old tunnel pauses new creation and retains progress");
    }
}
