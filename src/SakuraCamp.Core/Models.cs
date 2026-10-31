using System.Text.RegularExpressions;

namespace SakuraCamp.Core;

public sealed record WorldInfo(string Path, string Name, string Mode, int MaxPlayers, bool HasCaves, bool HasToken, bool HasPassword, DateTime Modified)
{
    public string Folder => System.IO.Path.GetFileName(Path);
    public string Detail => $"{Folder} · {(HasCaves ? "地上 + 洞穴" : "仅地上")} · {MaxPlayers} 人 · {(HasPassword ? "有密码" : "无密码")}";
    public override string ToString() => Name;
}

public sealed record ModInfo(string Id, string Name, string Version, bool Downloaded)
{
    public string Url => $"https://steamcommunity.com/sharedfiles/filedetails/?id={Id}";
    public string State => Downloaded ? "已下载" : "待下载";
}

public sealed record Endpoint(string Host, int Port)
{
    public static Endpoint Parse(string input)
    {
        var match = Regex.Match(input.Trim(), @"^(?<host>[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?):(?<port>\d{1,5})$");
        if (!match.Success || !int.TryParse(match.Groups["port"].Value, out var port) || port is < 1 or > 65535)
            throw new InvalidOperationException("请输入完整的樱花地址，例如 frp-example.com:44812；端口范围为 1–65535。");
        return new(match.Groups["host"].Value.ToLowerInvariant(), port);
    }
    public override string ToString() => $"{Host}:{Port}";
}

public sealed record LaunchPlan(WorldInfo World, string ServerExe, string WorkshopPath, Endpoint Master, Endpoint? Caves)
{
    public void Validate()
    {
        if (!File.Exists(ServerExe) || !System.IO.Path.GetFileName(ServerExe).Equals("dontstarve_dedicated_server_nullrenderer_x64.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择饥荒的 64 位独立服务器程序（dontstarve_dedicated_server_nullrenderer_x64.exe）。");
        if (World.HasCaves && Caves is null) throw new InvalidOperationException("这个存档包含洞穴，请填写洞穴隧道地址。");
        if (Caves is not null && (!Master.Host.Equals(Caves.Host, StringComparison.OrdinalIgnoreCase) || Master.Port == Caves.Port))
            throw new InvalidOperationException("地上和洞穴必须使用同一节点的域名，且端口不能相同。");
        foreach (var shard in Shards)
        {
            var path = System.IO.Path.Combine(World.Path, shard.Name);
            if (!File.Exists(System.IO.Path.Combine(path, "server.ini")) || !Directory.Exists(System.IO.Path.Combine(path, "save")))
                throw new InvalidOperationException($"{shard.Name} 缺少配置或存档。请先在游戏中生成世界，退出到主菜单后再导入。");
        }
        var ini = IniDocument.Load(System.IO.Path.Combine(World.Path, "cluster.ini"));
        if (ini.Get("NETWORK", "offline_cluster").Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前世界处于离线模式。请先在游戏中改为在线世界。");
        if (World.HasCaves && !ini.Get("SHARD", "shard_enabled").Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("存档有洞穴，但 shard_enabled 未开启。请在游戏中确认洞穴设置。");
        var tokenFile = System.IO.Path.Combine(World.Path, "cluster_token.txt");
        if (!File.Exists(tokenFile) || !IsToken(File.ReadAllText(tokenFile).Trim()))
            throw new InvalidOperationException("这个世界还没有有效格式的 Klei 服务器令牌，请先在世界设置中保存令牌。");
    }
    public IEnumerable<(string Name, int Port)> Shards
    {
        get
        {
            yield return ("Master", Master.Port);
            if (World.HasCaves && Caves is not null) yield return ("Caves", Caves.Port);
        }
    }
    public static bool IsToken(string token) => token.StartsWith("pds-", StringComparison.Ordinal) && token.Length > 20 && !token.Any(char.IsWhiteSpace);
}

public sealed record TunnelInfo(int Id, string Name, int Node, string Type, string LocalIp, int LocalPort, string Remote, bool Online, int Status)
{
    public string NodeHost { get; init; } = "";
    public string Note { get; init; } = "";
    public string ConnectionAddress => int.TryParse(Remote, out _) ? (NodeHost.Length > 0 ? $"{NodeHost}:{Remote}" : "") : Remote;
    public string Label => $"{Name}  ·  {Type.ToUpperInvariant()}  ·  节点 {Node}  ·  本地 {LocalPort} → {(ConnectionAddress.Length > 0 ? ConnectionAddress : Remote)}";
    public Endpoint GetEndpoint(string host = "")
    {
        if (Status != 0) throw new InvalidOperationException("这条隧道不可用，请先在樱花面板检查状态。");
        if (Type != "udp") throw new InvalidOperationException("饥荒需要 UDP 隧道，请在樱花面板修改。");
        if (LocalIp is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("隧道本地地址需要是 127.0.0.1（服务器运行在本机）。");
        var resolvedHost = NodeHost.Length > 0 ? NodeHost : host;
        if (int.TryParse(Remote, out _) && resolvedHost.Length == 0)
            throw new InvalidOperationException($"未能读取节点 {Node} 的连接域名，请重新读取隧道，或切换手动模式填写官方连接地址。");
        var endpoint = int.TryParse(Remote, out var port) ? Endpoint.Parse($"{resolvedHost}:{port}") : Endpoint.Parse(Remote);
        if (LocalPort != endpoint.Port)
            throw new InvalidOperationException("请先在樱花面板将隧道本地端口设为与远程端口一致，避免下洞穴时掉线。");
        return endpoint;
    }
}

public sealed class AppSettings
{
    public string SaveRoot { get; set; } = "";
    public string ServerExe { get; set; } = "";
    public string WorkshopPath { get; set; } = "";
    public string FrpcExe { get; set; } = "";
    public string LastWorld { get; set; } = "";
    public Dictionary<string, WorldProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WorldProfile
{
    // Kept only to read older settings; one-click launch always starts the tunnels.
    public bool? AutomaticTunnels { get; set; }
    public string MasterAddress { get; set; } = "";
    public string CavesAddress { get; set; } = "";
    public int? MasterTunnelId { get; set; }
    public int? CavesTunnelId { get; set; }
    public string CreationKey { get; set; } = "";
    public int? CreationNodeId { get; set; }
    public NodeInfo? SavedNode { get; set; }
    public bool PublicServer { get; set; }
    public HashSet<string> PendingTunnelShards { get; set; } = [];
}

public sealed record NodeInfo(int Id, string Name, string Host, int Vip, int Flag)
{
    public bool CanCreate => (Flag & 4) != 0 && (Flag & 32) != 0 && (Flag & 512) == 0;
    public bool AllowsPublicGame => (Flag & 2048) != 0 || Name.Contains("多线", StringComparison.Ordinal);
    public bool IsAvailable(bool publicServer) => CanCreate && (!publicServer || AllowsPublicGame);
    public string Label => Flag == 0 ? Name + (Host.Length > 0 ? " · " + Host : "")
        : $"{Name} · {(Vip == 0 ? "普通账号" : $"需 VIP {Vip}")} · {Host}";
}

public sealed record TunnelPair(TunnelInfo Master, TunnelInfo? Caves);

public sealed record CreationRecovery(int FoundCount, IReadOnlyList<string> MissingNames, bool ConfirmationRequired, bool NodeUnlocked);
