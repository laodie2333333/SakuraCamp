using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SakuraCamp.Core;

public sealed partial class SakuraClient(HttpClient client)
{
    public async Task WaitForOnlineAsync(string token, IReadOnlyList<int> ids, TimeSpan? timeout = null,
        TimeSpan? pollInterval = null, Action<string>? progress = null)
    {
        ValidateToken(token);
        if (ids.Count == 0 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count)
            throw new InvalidOperationException("需要核验不同的有效通道编号。");
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (true)
        {
            // Online verification needs only /tunnels, not the slower node hostname lookup.
            var tunnels = await TunnelStatesAsync(token);
            var chosen = ids.Select(id => tunnels.SingleOrDefault(t => t.Id == id)
                ?? throw new InvalidOperationException($"通道 {id} 已不存在，已停止启动世界。")).ToArray();
            if (chosen.Any(t => t.Status != 0)) throw new InvalidOperationException("所选通道已被禁用或不可用，已停止启动世界。");
            if (chosen.All(t => t.Online))
            {
                progress?.Invoke("官方接口已确认所选 " + chosen.Length + " 条通道全部在线。");
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline)
                throw new InvalidOperationException("官方接口未确认通道在线：" + string.Join("、", chosen.Where(t => !t.Online).Select(t => t.Name)) + "。已停止启动世界，请查看本工具的樱花通道日志。");
            await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(1));
        }
    }

    public async Task<IReadOnlyList<TunnelInfo>> TunnelStatesAsync(string token)
    {
        ValidateToken(token);
        using var document = await ReadAsync("tunnels", token);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("樱花通道状态接口格式异常。");
        return document.RootElement.EnumerateArray().Select(ParseTunnel).ToArray();
    }

    public static TunnelPair SelectWorldTunnels(IReadOnlyList<TunnelInfo> tunnels, WorldProfile profile, bool hasCaves,
        int? selectedMasterId = null, int? selectedCavesId = null)
    {
        TunnelInfo Select(string shard, int? id, string address)
        {
            var candidates = id.HasValue ? tunnels.Where(t => t.Id == id.Value).ToArray()
                : Guid.TryParseExact(profile.CreationKey, "N", out _) ? tunnels.Where(t => t.Note == $"SakuraCamp:{profile.CreationKey}:{shard}").ToArray()
                : address.Length > 0 ? tunnels.Where(t => t.ConnectionAddress.Equals(address, StringComparison.OrdinalIgnoreCase)).ToArray() : [];
            if (candidates.Length != 1)
                throw new InvalidOperationException($"未能唯一确定{(shard == "Master" ? "地上" : "洞穴")}隧道。请在联机设置中读取并选择当前世界的隧道；一键开服会同时启动它们。");
            _ = candidates[0].GetEndpoint();
            return candidates[0];
        }
        var master = Select("Master", selectedMasterId ?? profile.MasterTunnelId, profile.MasterAddress);
        var caves = hasCaves ? Select("Caves", selectedCavesId ?? profile.CavesTunnelId, profile.CavesAddress) : null;
        if (caves is not null && (master.Id == caves.Id || master.Node != caves.Node || master.GetEndpoint().Host != caves.GetEndpoint().Host || master.LocalPort == caves.LocalPort))
            throw new InvalidOperationException("地上和洞穴需要使用同一节点的两条不同 UDP 隧道，且端口不同。");
        return new(master, caves);
    }

    private sealed class RequestRejectedException(string message) : InvalidOperationException(message);
    private static void ValidateToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace)) throw new InvalidOperationException("请填写樱花访问密钥（不是 Klei 令牌或网站密码）。");
    }
    public async Task<IReadOnlyList<TunnelInfo>> TunnelsAsync(string token)
    {
        ValidateToken(token);
        using var document = await ReadAsync("tunnels", token);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("樱花接口返回格式发生变化，无法读取通道，请稍后重试。");
        var tunnels = document.RootElement.EnumerateArray().Select(ParseTunnel).ToArray();
        if (!tunnels.Any(t => int.TryParse(t.Remote, out _))) return tunnels;
        using var nodes = await ReadAsync("nodes", token);
        if (nodes.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("樱花节点接口返回格式发生变化，无法自动生成连接地址。请稍后重试。");
        return tunnels.Select(t =>
        {
            if (!int.TryParse(t.Remote, out _)) return t;
            if (!nodes.RootElement.TryGetProperty(t.Node.ToString(System.Globalization.CultureInfo.InvariantCulture), out var node) ||
                node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.String)
                return t;
            return t with { NodeHost = host.GetString()?.Trim() ?? "" };
        }).ToArray();
    }
    private async Task<JsonDocument> ReadAsync(string resource, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.natfrp.com/v4/" + resource);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"樱花{(resource == "nodes" ? "节点" : "隧道")}接口返回 {(int)response.StatusCode}，请检查访问密钥和网络后重新读取。");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    public static TunnelInfo ParseTunnel(JsonElement el)
    {
        string Text(string key) => el.TryGetProperty(key, out var value) ? value.ToString() : "";
        int Number(string key) => int.TryParse(Text(key), out var number) ? number : 0;
        return new(Number("id"), Text("name"), Number("node"), Text("type"), Text("local_ip"), Number("local_port"), Text("remote"), Text("online").Equals("True", StringComparison.OrdinalIgnoreCase), Number("status")) { Note = Text("note") };
    }
    public async Task<IReadOnlyList<NodeInfo>> NodesAsync(string token)
    {
        ValidateToken(token);
        using var document = await ReadAsync("nodes", token);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("节点列表格式发生变化，请重试。");
        var result = new List<NodeInfo>();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var id) || entry.Value.ValueKind != JsonValueKind.Object) continue;
            string Text(string key) => entry.Value.TryGetProperty(key, out var v) ? v.ToString() : "";
            int Number(string key) => int.TryParse(Text(key), out var v) ? v : 0;
            result.Add(new(id, Text("name"), Text("host"), Number("vip"), Number("flag")));
        }
        return result;
    }

    // Stable names and a persisted ownership marker allow retries after a partially completed request.
    // Existing unrelated tunnels are never edited, migrated or deleted.
    public async Task<TunnelPair> EnsureWorldTunnelsAsync(string token, int nodeId, bool publicServer, string creationKey, bool caves, Action<string>? progress = null,
        HashSet<string>? pendingShards = null, Action? persistPending = null)
    {
        pendingShards ??= [];
        ValidateToken(token);
        if (!Guid.TryParseExact(creationKey, "N", out _)) throw new InvalidOperationException("世界隧道标识无效，请重新保存方案。");
        var node = (await NodesAsync(token)).FirstOrDefault(n => n.Id == nodeId) ?? throw new InvalidOperationException("该节点已不存在，请重新读取可用节点。");
        if (!node.IsAvailable(publicServer)) throw new InvalidOperationException("所选节点目前不支持创建 UDP 隧道，或不允许公开游戏服务器。请重新选择。");
        _ = Endpoint.Parse($"{node.Host}:1");
        var marker = "SakuraCamp:" + creationKey;
        async Task<TunnelInfo> Ensure(string shard, int temporaryPort)
        {
            var name = "sc" + creationKey[..10] + (shard == "Master" ? "m" : "c");
            var note = marker + ":" + shard;
            var tunnels = await TunnelsAsync(token);
            var matches = tunnels.Where(t => t.Name == name || t.Note == note).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException($"{shard} 存在多个匹配的隧道，请在面板核对，工具没有修改它们。");
            var tunnel = matches.SingleOrDefault();
            if (tunnel is not null && (tunnel.Note != note || tunnel.Name != name || tunnel.Node != nodeId || tunnel.Type != "udp" || tunnel.Status != 0 || tunnel.LocalIp != "127.0.0.1"))
                throw new InvalidOperationException($"已有 {shard} 隧道与本次方案不一致，请选择原节点继续或手动核对，工具没有更改它。");
            if (tunnel is null)
            {
                if (pendingShards.Contains(shard))
                    throw new InvalidOperationException($"上次 {shard} 创建请求结果尚不明确。请在官方面板检查名称 {name}；若没有该隧道，点击“检查并恢复创建”解除待确认状态。");
                progress?.Invoke($"正在为 {shard} 创建 UDP 隧道，远程端口由樱花分配…");
                pendingShards.Add(shard); persistPending?.Invoke();
                // Use a valid temporary local port; align it with the assigned remote port before launching.
                JsonDocument createdResponse;
                try
                {
                    createdResponse = await PostAsync("tunnels", token, new()
                    {
                        ["name"] = name, ["type"] = "udp", ["node"] = nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["note"] = note, ["local_ip"] = "127.0.0.1", ["local_port"] = temporaryPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    });
                }
                catch (RequestRejectedException) { pendingShards.Remove(shard); persistPending?.Invoke(); throw; }
                using var created = createdResponse;
                if (created.RootElement.TryGetProperty("important_warning_for_ai", out var warning) && warning.ValueKind == JsonValueKind.String)
                    progress?.Invoke("樱花接口提示：" + LogReader.Redact(warning.GetString() ?? "", token));
                if (!created.RootElement.TryGetProperty("id", out var createdId) || !createdId.TryGetInt32(out var id) || id <= 0)
                    throw new InvalidOperationException("创建接口未返回有效隧道编号。请重新读取列表后再继续，不要反复创建。");
                tunnels = await TunnelsAsync(token);
                tunnel = tunnels.SingleOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("隧道已创建，但列表暂未返回。稍候再次点击创建可继续补齐。");
                if (tunnel.Note != note || tunnel.Name != name || tunnel.Node != nodeId || tunnel.Type != "udp" || tunnel.Status != 0 || tunnel.LocalIp != "127.0.0.1")
                    throw new InvalidOperationException("已创建隧道与本次请求不符，请在面板核对后继续。");
            }
            else progress?.Invoke($"已找到 {shard} 隧道，继续完成配置。");
            if (pendingShards.Remove(shard)) persistPending?.Invoke();
            var address = Endpoint.Parse(tunnel.ConnectionAddress);
            if (!address.Host.Equals(node.Host, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("隧道返回的域名与所选节点不符，请重新读取。");
            if (tunnel.LocalPort != address.Port)
            {
                if (tunnel.Online) throw new InvalidOperationException("待修复的隧道已在线，请先停止该隧道再继续。");
                using var edited = await PostAsync("tunnel/edit", token, new()
                { ["id"] = tunnel.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), ["local_port"] = address.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                tunnel = (await TunnelsAsync(token)).SingleOrDefault(t => t.Id == tunnel.Id)
                    ?? throw new InvalidOperationException("隧道配置已提交，但暂未读取到。请稍候重试。");
            }
            if (tunnel.Note != note || tunnel.Name != name || tunnel.Node != nodeId ||
                !Endpoint.Parse(tunnel.ConnectionAddress).Host.Equals(node.Host, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("隧道配置在操作期间发生变化，请重新读取并核对。");
            _ = tunnel.GetEndpoint();
            progress?.Invoke($"{shard} 已就绪：{tunnel.ConnectionAddress}");
            return tunnel;
        }
        try
        {
            var master = await Ensure("Master", 10999);
            var cave = caves ? await Ensure("Caves", 10998) : null;
            if (cave is not null && (master.Id == cave.Id || master.GetEndpoint().Port == cave.GetEndpoint().Port))
                throw new InvalidOperationException("地上与洞穴的隧道或端口重复，请在官方面板核对。");
            return new(master, cave);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException("隧道配置尚未全部完成：" + ex.Message + "\n已创建的隧道会保留。使用同一节点再次点击可继续补齐；工具不会自动删除已有隧道。", ex);
        }
    }

    public async Task<CreationRecovery> RecoverCreationAsync(string token, WorldProfile profile, bool confirmedAbsent, Action? persist = null)
    {
        if (!Guid.TryParseExact(profile.CreationKey, "N", out _))
            throw new InvalidOperationException("当前世界还没有自动创建记录，请先选择节点并创建隧道。");
        // Always read again after the user's confirmation; no remote mutation is performed here.
        var tunnels = await TunnelsAsync(token);
        var shards = new[] { "Master", "Caves" };
        string Name(string shard) => "sc" + profile.CreationKey[..10] + (shard == "Master" ? "m" : "c");
        bool Matches(TunnelInfo tunnel, string shard) => tunnel.Name == Name(shard) ||
            tunnel.Note == "SakuraCamp:" + profile.CreationKey + ":" + shard ||
            tunnel.Id == (shard == "Master" ? profile.MasterTunnelId : profile.CavesTunnelId);
        var found = tunnels.Where(t => shards.Any(s => Matches(t, s))).ToArray();
        var missing = profile.PendingTunnelShards.Where(s => shards.Contains(s) && !found.Any(t => Matches(t, s)))
            .Select(Name).ToArray();
        if (profile.PendingTunnelShards.Any(s => !shards.Contains(s)))
            throw new InvalidOperationException("待确认记录格式无效，请保留设置并联系开发者。");
        if (missing.Length > 0 && !confirmedAbsent) return new(found.Length, missing, true, false);
        profile.PendingTunnelShards.Clear();
        // Keep the ownership key stable so a delayed create response can still be recognised.
        if (found.Length == 0) profile.CreationNodeId = null;
        persist?.Invoke();
        return new(found.Length, missing, false, found.Length == 0);
    }

    private async Task<JsonDocument> PostAsync(string resource, string token, Dictionary<string, string> values)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.natfrp.com/v4/" + resource);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new FormUrlEncodedContent(values);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        JsonDocument? parsed = null;
        try { parsed = JsonDocument.Parse(body); } catch (JsonException) { }
        using var errorDocument = parsed;
        var businessCode = 0;
        if (parsed?.RootElement.ValueKind == JsonValueKind.Object && parsed.RootElement.TryGetProperty("code", out var code))
            _ = int.TryParse(code.ToString(), out businessCode);
        if (!response.IsSuccessStatusCode || businessCode >= 400)
        {
            var reason = "请检查实名认证、隧道额度、节点权限和账号状态。";
            if (parsed?.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in new[] { "message", "error", "msg" })
                    if (parsed.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                    { reason = v.GetString()![..Math.Min(v.GetString()!.Length, 400)]; break; }
            }
            var codeText = businessCode > 0 ? $"（错误代码 {businessCode}）" : "";
            var message = LogReader.Redact($"樱花创建/配置接口返回 HTTP {(int)response.StatusCode}{codeText}：{reason}", token);
            if (businessCode is >= 400 and < 500 and not 408 || (int)response.StatusCode is >= 400 and < 500 and not 408)
                throw new RequestRejectedException(message);
            throw new InvalidOperationException(message);
        }
        if (parsed?.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("樱花创建/配置接口返回格式异常，操作结果尚不明确。请使用“检查并恢复创建”核对。");
        return JsonDocument.Parse(body);
    }
    public static ProcessStartInfo StartInfo(string frpcExe, string token, IEnumerable<int> ids)
    {
        if (!File.Exists(frpcExe) || !Path.GetFileName(frpcExe).Equals("frpc.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请选择樱花官方 frpc.exe。");
        if (FrpcInstaller.IsLauncherCompanion(frpcExe)) throw new InvalidOperationException("不能启动樱花启动器专用 frpc。请下载官方独立版，或使用手动模式。");
        var values = ids.ToArray();
        if (values.Length == 0 || values.Any(i => i <= 0) || values.Distinct().Count() != values.Length) throw new InvalidOperationException("请选择不同的有效隧道。");
        var info = WorldFiles.HiddenProcess(frpcExe);
        info.ArgumentList.Add("--disable_log_color");
        info.Environment["NATFRP_TOKEN"] = token;
        info.Environment["NATFRP_TARGET"] = string.Join(",", values);
        return info;
    }
}
