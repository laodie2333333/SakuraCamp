using System.Globalization;

namespace SakuraCamp.Core;

public sealed partial class SakuraClient
{
    public async Task<TunnelPair> ChangeWorldNodeAsync(string token, WorldProfile profile, int nodeId, bool caves,
        Action persist, Action<string>? progress = null)
    {
        ValidateToken(token);
        if (profile.NodeChange is { } unfinished && unfinished.Target.Id != nodeId)
            throw new InvalidOperationException($"节点更换尚未完成，请先选择节点 {unfinished.Target.Id} 继续，避免遗留通道。");
        var target = (await NodesAsync(token)).SingleOrDefault(n => n.Id == nodeId)
            ?? throw new InvalidOperationException("所选节点不存在，请重新读取节点。");
        if (!target.IsAvailable(profile.PublicServer)) throw new InvalidOperationException("所选节点当前不可用或不支持此用途，原通道尚未删除。");
        _ = Endpoint.Parse($"{target.Host}:1");

        if (profile.NodeChange is null)
        {
            if (profile.PendingTunnelShards.Count > 0)
            {
                var recovery = await RecoverCreationAsync(token, profile, false, persist);
                if (recovery.ConfirmationRequired)
                    throw new InvalidOperationException("上次创建请求尚未确认，请先恢复未完成的隧道配置，再选择新节点。");
            }
            var all = await TunnelsAsync(token);
            var owned = FindOwnedForReplacement(all, profile);
            var oldNode = profile.CreationNodeId ?? owned.FirstOrDefault()?.Node;
            if (oldNode == nodeId && Guid.TryParseExact(profile.CreationKey, "N", out _))
            {
                var same = await EnsureWorldTunnelsAsync(token, nodeId, profile.PublicServer, profile.CreationKey, caves,
                    progress, profile.PendingTunnelShards, persist);
                Bind(profile, target, same); persist(); return same;
            }
            profile.NodeChange = new()
            {
                Target = target, OldCreationKey = profile.CreationKey, NewCreationKey = Guid.NewGuid().ToString("N"), OldTunnels = owned.ToList()
            };
            persist(); // The deletion set and replacement ownership must survive a restart before any remote write.
        }

        var change = profile.NodeChange;
        if (!change.OldDeleted)
        {
            var current = await TunnelsAsync(token);
            var remaining = current.Where(t => change.OldTunnels.Any(old => old.Id == t.Id)).ToArray();
            foreach (var tunnel in remaining)
            {
                var old = change.OldTunnels.Single(t => t.Id == tunnel.Id);
                if (tunnel.Name != old.Name || tunnel.Note != old.Note || tunnel.Node != old.Node || tunnel.Type != "udp" || tunnel.LocalIp != old.LocalIp)
                    throw new InvalidOperationException("旧通道归属或节点已发生变化，已停止更换，未继续删除通道。");
            }
            if (remaining.Length > 0)
            {
                progress?.Invoke($"正在删除当前世界的 {remaining.Length} 条旧通道…");
                using var response = await PostAsync("tunnel/delete", token, new()
                { ["ids"] = string.Join(",", remaining.Select(t => t.Id.ToString(CultureInfo.InvariantCulture))) });
                var root = response.RootElement;
                if (!root.TryGetProperty("deleted", out var deleted) || deleted.ValueKind != System.Text.Json.JsonValueKind.Array ||
                    !root.TryGetProperty("failed", out var failed) || failed.ValueKind != System.Text.Json.JsonValueKind.Array)
                    throw new InvalidOperationException("删除结果格式异常，已保留更换记录；选择同一目标节点可核对后继续。");
                var deletedIds = deleted.EnumerateArray().Select(v => v.GetInt32()).ToHashSet();
                if (failed.GetArrayLength() > 0 || remaining.Any(t => !deletedIds.Contains(t.Id)))
                    throw new InvalidOperationException("旧通道删除或下线尚未全部成功，尚未创建新通道。请核对后选择同一目标节点继续。");
            }
            var after = await TunnelsAsync(token);
            if (after.Any(t => change.OldTunnels.Any(old => old.Id == t.Id)))
                throw new InvalidOperationException("官方列表仍显示旧通道，已暂停创建。稍候选择同一目标节点继续。");
            change.OldDeleted = true;
            profile.CreationKey = change.NewCreationKey; profile.CreationNodeId = target.Id; profile.SavedNode = target;
            profile.MasterTunnelId = profile.CavesTunnelId = null;
            profile.MasterAddress = profile.CavesAddress = "";
            profile.PendingTunnelShards.Clear(); persist();
        }
        progress?.Invoke("旧通道已清理，正在为所选节点创建并绑定新通道…");
        var pair = await EnsureWorldTunnelsAsync(token, target.Id, profile.PublicServer, change.NewCreationKey, caves,
            progress, profile.PendingTunnelShards, persist);
        Bind(profile, target, pair);
        profile.NodeChange = null; persist();
        progress?.Invoke("节点更换完成，当前世界已绑定新通道。");
        return pair;
    }

    private static IReadOnlyList<TunnelInfo> FindOwnedForReplacement(IReadOnlyList<TunnelInfo> all, WorldProfile profile)
    {
        var key = profile.CreationKey;
        var validKey = Guid.TryParseExact(key, "N", out _);
        var found = new List<TunnelInfo>();
        foreach (var shard in new[] { "Master", "Caves" })
        {
            var id = shard == "Master" ? profile.MasterTunnelId : profile.CavesTunnelId;
            var name = validKey ? "sc" + key[..10] + (shard == "Master" ? "m" : "c") : "";
            var note = validKey ? "SakuraCamp:" + key + ":" + shard : "";
            var matches = all.Where(t => t.Id == id || validKey && (t.Note == note || t.Name == name)).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("当前世界存在重复或冲突的通道记录，原通道未删除。请先在樱花面板核对。");
            if (matches.SingleOrDefault() is not { } tunnel) continue;
            if (!validKey || tunnel.Note != note || tunnel.Name != name || tunnel.Type != "udp" || tunnel.LocalIp != "127.0.0.1" ||
                profile.CreationNodeId is { } node && tunnel.Node != node)
                throw new InvalidOperationException("无法确认旧通道是程序为当前世界创建的通道，已停止自动删除。请先核对通道归属。");
            found.Add(tunnel);
        }
        if (found.Select(t => t.Id).Distinct().Count() != found.Count || found.Select(t => t.Node).Distinct().Count() > 1)
            throw new InvalidOperationException("旧通道的编号或节点不一致，已停止更换。");
        return found;
    }

    private static void Bind(WorldProfile profile, NodeInfo node, TunnelPair pair)
    {
        profile.CreationNodeId = node.Id; profile.SavedNode = node; profile.AutomaticTunnels = true;
        profile.MasterTunnelId = pair.Master.Id; profile.CavesTunnelId = pair.Caves?.Id;
        profile.MasterAddress = pair.Master.ConnectionAddress; profile.CavesAddress = pair.Caves?.ConnectionAddress ?? "";
    }
}
