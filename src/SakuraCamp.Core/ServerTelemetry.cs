using System.Text.Json;

namespace SakuraCamp.Core;

public sealed record PlayerStatus(string UserId, string Name, string Character, string Shard, double? PingMilliseconds, bool InWorld)
{
    public int? NetworkScore { get; init; }
    public VitalStatus? Health { get; init; }
    public VitalStatus? Hunger { get; init; }
    public VitalStatus? Sanity { get; init; }
    public bool Ghost { get; init; }
    public string Location => (Shard == "Master" ? "地上" : "洞穴") + (InWorld ? "" : " · 选角/连接中");
    public string PingText => PingMilliseconds is { } ping ? $"{ping:0} ms" : NetworkScore switch
    { 0 => "网络良好", 1 => "网络一般", >= 2 => "网络较差", _ => "等待网络数据" };
}

public sealed record VitalStatus(double Current, double Maximum)
{
    public double Percent => Math.Clamp(Current / Maximum * 100, 0, 100);
    public string Text => $"{Current:0.#} / {Maximum:0.#}";
}

public sealed record ServerSample(DateTimeOffset CapturedAt, int Day, string Season, bool Paused, IReadOnlyList<PlayerStatus> Players);
public sealed record ShardStatus(string Shard, bool Running, ServerSample? Sample, string Detail, double? CpuPercent, long? MemoryBytes);
public sealed record SessionStatus(DateTimeOffset CapturedAt, TimeSpan Uptime, IReadOnlyList<ShardStatus> Shards);

public static class ServerTelemetry
{
    public static string Marker(string requestId) => "[SakuraCamp.Status:" + requestId + "]";

    // A fixed, read-only console query. No world files, mods, tasks, or game state are changed.
    public static string Command(string requestId)
    {
        if (!Guid.TryParseExact(requestId, "N", out _)) throw new ArgumentException("Invalid status request ID", nameof(requestId));
        var lua = """
            do local ok,data=pcall(function()
            if not TheWorld or not TheWorld.state or not TheWorld.state.season then error("world not ready") end
            local players={} local present={} for _,p in ipairs(AllPlayers or {}) do if p.userid then present[p.userid]=p end end
            local function vital(c,cur,max) if not c then return nil end local a=c[cur] local b=c[max]
            if c.GetMaxWithPenalty then local ok,v=pcall(c.GetMaxWithPenalty,c) if ok then b=v end end
            if type(a)=="number" and type(b)=="number" and b>0 then return {current=a,max=b} end end
            for _,v in ipairs(TheNet:GetClientTable() or {}) do
            if v.performance==nil and v.userid and v.userid~="" then
            local p=present[v.userid] local c=p and p.components or {} local ghost=p and p:HasTag("playerghost") or false
            players[#players+1]={userid=v.userid,name=v.name or "",character=p and p.prefab or v.prefab or "",ping=tonumber(v.ping),netscore=tonumber(v.netscore),inworld=p~=nil,ghost=ghost,
            health=not ghost and vital(c.health,"currenthealth","maxhealth") or nil,hunger=not ghost and vital(c.hunger,"current","max") or nil,sanity=not ghost and vital(c.sanity,"current","max") or nil}
            end end
            local s=TheWorld.state
            return {players=players,day=(s.cycles or 0)+1,season=s.season or "",paused=TheNet:IsServerPaused()}
            end)
            local j=require("json") print("__MARKER__"..j.encode_compliant(ok and data or {error="status query unavailable"})) end
            """;
        return lua.Replace("__MARKER__", Marker(requestId), StringComparison.Ordinal).ReplaceLineEndings(" ");
    }

    public static bool TryParse(string line, string requestId, string shard, out ServerSample? sample)
    {
        sample = null;
        var marker = Marker(requestId);
        var offset = line.IndexOf(marker, StringComparison.Ordinal);
        if (offset < 0 || line.Length > 128 * 1024) return false;
        var json = line[(offset + marker.Length)..].Trim();
        // Console echoes contain Lua after the marker, rather than a JSON object.
        if (!json.StartsWith('{')) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("error", out _) || !root.TryGetProperty("players", out var players) ||
                !root.TryGetProperty("day", out var day) || !day.TryGetInt32(out var dayNumber) || dayNumber < 1 ||
                !root.TryGetProperty("season", out var season) || season.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("paused", out var paused) || paused.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
            var rows = new List<PlayerStatus>();
            if (players.ValueKind == JsonValueKind.Array)
            {
                if (players.GetArrayLength() > 256) return false;
                foreach (var player in players.EnumerateArray())
                {
                    if (!player.TryGetProperty("userid", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString())) return false;
                    string Text(string key) => player.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                    double? ping = player.TryGetProperty("ping", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var n) && double.IsFinite(n) && n >= 0 ? n : null;
                    VitalStatus? Vital(string key)
                    {
                        if (!player.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Object ||
                            !v.TryGetProperty("current", out var a) || !v.TryGetProperty("max", out var b) ||
                            a.ValueKind != JsonValueKind.Number || b.ValueKind != JsonValueKind.Number ||
                            !a.TryGetDouble(out var current) || !b.TryGetDouble(out var maximum) ||
                            !double.IsFinite(current) || !double.IsFinite(maximum) || maximum <= 0) return null;
                        return new(current, maximum);
                    }
                    rows.Add(new(id.GetString()!, Text("name"), Text("character"), shard, ping,
                        player.TryGetProperty("inworld", out var active) && active.ValueKind == JsonValueKind.True)
                    {
                        NetworkScore = player.TryGetProperty("netscore", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetInt32(out var network) && network >= 0 ? network : null,
                        Ghost = player.TryGetProperty("ghost", out var ghost) && ghost.ValueKind == JsonValueKind.True,
                        Health = Vital("health"), Hunger = Vital("hunger"), Sanity = Vital("sanity")
                    });
                }
            }
            else if (players.ValueKind != JsonValueKind.Object || players.EnumerateObject().Any()) return false; // Lua encodes an empty table as {}.
            sample = new(DateTimeOffset.UtcNow, dayNumber, season.GetString()!, paused.GetBoolean(), rows.DistinctBy(p => p.UserId).ToArray());
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return false; }
    }
}
