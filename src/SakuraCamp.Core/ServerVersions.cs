using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SakuraCamp.Core;

public sealed record ServerInstallation(string Executable, string Root, long? Version, string AppId, long? SteamBuild, string Branch, bool Complete)
{
    public string SteamManifest { get; init; } = "";
    public bool ManagedBySteam => SteamManifest.Length > 0;
}
public sealed record ShardVersion(string Shard, long? GeneratedVersion, string Snapshot);
public sealed record ServerVersionCheck(ServerInstallation Server, ServerInstallation? Client, IReadOnlyList<ShardVersion> Shards)
{
    public long MinimumVersion => Shards.Max(s => s.GeneratedVersion ?? 0);
    public string Summary => $"服务端 {Server.Version?.ToString() ?? "未知"} · 游戏 {Client?.Version?.ToString() ?? "未发现"} · " +
        string.Join(" · ", Shards.Select(s => $"{(s.Shard == "Master" ? "地上" : "洞穴")}生成版本 {s.GeneratedVersion?.ToString() ?? "未知"}"));
}

// Read data only. World snapshots are Lua source but must never be executed for a version check.
public static class ServerVersions
{
    public const string ExecutableName = "dontstarve_dedicated_server_nullrenderer_x64.exe";
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private static Match Match(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.CultureInvariant, MatchTimeout);
    private static string ReadBounded(string path, int maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(file.Length, maximum)];
        file.ReadExactly(bytes);
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }
    private static string Field(string text, string key) => Match(text, "\"" + Regex.Escape(key) + "\"\\s*\"([^\"]*)\"").Groups[1].Value;
    public static ServerInstallation Installation(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return new("", "", null, "343050", null, "public", false);
        var full = Path.GetFullPath(executable);
        if (!Path.GetFileName(full).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请选择饥荒的 64 位服务器程序。版本检查不会运行其他程序。");
        var root = Directory.GetParent(Path.GetDirectoryName(full)!)?.FullName ?? throw new InvalidOperationException("服务端路径无效。");
        long? version = null;
        var versionFile = Path.Combine(root, "version.txt");
        if (File.Exists(versionFile) && long.TryParse(ReadBounded(versionFile, 128).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0) version = number;
        var library = Directory.GetParent(root)?.Parent?.FullName;
        foreach (var app in new[] { "322330", "343050" })
        {
            foreach (var manifest in new[] { library is null ? "" : Path.Combine(library, $"appmanifest_{app}.acf"), Path.Combine(root, "steamapps", $"appmanifest_{app}.acf") })
            {
                if (!File.Exists(manifest)) continue;
                var content = ReadBounded(manifest, 256 * 1024);
                // A sibling manifest is relevant only when it names this exact installation.
                if (Path.GetDirectoryName(manifest) == library && !Field(content, "installdir").Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase)) continue;
                var branch = Field(content, "BetaKey");
                var complete = Field(content, "StateFlags") == "4";
                return new(full, root, version, app, long.TryParse(Field(content, "buildid"), out var build) && build > 0 ? build : null,
                    branch.Length == 0 ? "public" : branch, complete && File.Exists(full))
                { SteamManifest = Path.GetDirectoryName(manifest) == library ? manifest : "" };
            }
        }
        // A missing manifest must not turn a game installation into a standalone update target.
        var game = File.Exists(Path.Combine(root, "bin64", "dontstarve_steam_x64.exe")) ||
            File.Exists(Path.Combine(root, "bin", "dontstarve_steam.exe")) ||
            Path.GetFileName(root).Equals("Don't Starve Together", StringComparison.OrdinalIgnoreCase);
        return new(full, root, version, game ? "322330" : "343050", null, "public", false);
    }

    public static ServerVersionCheck Inspect(WorldInfo world, string executable, IEnumerable<string> clientExecutables)
    {
        var server = Installation(executable);
        var clients = clientExecutables.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Select(Installation).ToArray();
        var client = server.AppId == "322330" ? server : clients.OrderByDescending(c => c.Version ?? 0).FirstOrDefault();
        var shards = (world.HasCaves ? new[] { "Master", "Caves" } : new[] { "Master" }).Select(shard => ReadShard(world.Path, shard)).ToArray();
        return new(server, client, shards);
    }

    public static ShardVersion ReadShard(string world, string shard)
    {
        var save = Path.Combine(world, shard, "save");
        var sessions = Path.Combine(save, "session");
        if (!Directory.Exists(sessions)) return new(shard, null, "");
        var index = Path.Combine(save, "shardindex");
        var id = File.Exists(index) ? Match(ReadBounded(index, 1024 * 1024), "session_id\\s*=\\s*\"([A-Fa-f0-9]{16})\"").Groups[1].Value : "";
        var directories = id.Length > 0 ? new[] { Path.Combine(sessions, id) } : Directory.GetDirectories(sessions)
            .Where(p => Match(Path.GetFileName(p), "^[A-Fa-f0-9]{16}$").Success && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).ToArray();
        // World snapshots are direct children. Player snapshots in nested directories are unrelated.
        var candidates = directories.Where(Directory.Exists).Select(directory => Directory.GetFiles(directory)
            .Where(p => Match(Path.GetFileName(p), "^[0-9]{10}$").Success).OrderByDescending(p => Path.GetFileName(p), StringComparer.Ordinal).FirstOrDefault())
            .Where(p => p is not null).Cast<string>();
        var snapshot = candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (snapshot is null) return new(shard, null, "");
        // meta is normally near the beginning, but large preceding entity tables are valid.
        using var stream = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var buffer = new char[32768]; var overlap = ""; var total = 0;
        while (total < 64 * 1024 * 1024)
        {
            var count = reader.Read(buffer, 0, buffer.Length); if (count == 0) break;
            total += count; var text = overlap + new string(buffer, 0, count);
            // Match the generated meta table, not similarly named fields in Mod or entity data.
            var match = Match(text, "(?:tablefunctions\\[\"meta_fn\"\\]\\s*=\\s*function\\s*\\(\\)\\s*return|(?:\\[\"meta\"\\]|\\bmeta)\\s*=)\\s*\\{[^{}]{0,4096}?\\bbuild_version\\s*=\\s*\"?([0-9]+)");
            if (match.Success && long.TryParse(match.Groups[1].Value, out var version) && version > 0) return new(shard, version, snapshot);
            overlap = text.Length > 8192 ? text[^8192..] : text;
        }
        return new(shard, null, snapshot);
    }

    public static void RequireReadable(ServerVersionCheck check)
    {
        if (check.Server.Branch != "public" || check.Client is { Branch: not "public" })
            throw new InvalidOperationException("检测到测试版或其他 Steam 分支。当前自动更新仅支持正式版；请先在 Steam 中将游戏和独立服务端的“测试版”都设为“无”，再检查更新。不会自动切换分支或降级存档。");
        if (check.Shards.Any(s => s.GeneratedVersion is null))
            throw new InvalidOperationException("无法读取" + string.Join("、", check.Shards.Where(s => s.GeneratedVersion is null).Select(s => s.Shard == "Master" ? "地上" : "洞穴")) +
                "当前存档的生成版本，已阻止开服。请确认已在游戏中生成并保存世界；压缩、损坏或未知格式的存档不能自动判定兼容性，原文件未修改。");
        if (check.Client is { Complete: false })
            throw new InvalidOperationException("本机饥荒游戏尚未安装完整或有待更新任务，请先在 Steam 完成游戏更新与文件校验，再开服。");
        if (check.Client is { Version: null })
            throw new InvalidOperationException("发现本机饥荒游戏，但无法读取其 version.txt，已阻止开服。请在 Steam 验证游戏文件完整性后重试。");
    }

    public static void RequireCompatible(ServerVersionCheck check)
    {
        RequireReadable(check);
        if (check.Server.Version is not { } version || !check.Server.Complete)
            throw new InvalidOperationException("服务端版本或安装完整性无法确认，请先更新并校验服务端。");
        if (version < check.MinimumVersion)
            throw new InvalidOperationException($"服务端版本 {version} 低于世界生成版本 {check.MinimumVersion}，已阻止开服。请更新服务端；若已是正式版最新版本，请核对世界是否来自测试版。不会降级或修改世界版本。");
        if (check.Client?.Version is { } client && version != client)
            throw new InvalidOperationException($"服务端版本 {version} 与本机游戏版本 {client} 不一致，已阻止开服。请在 Steam 更新游戏本体，并确认双方使用正式版。不会通过降级服务端迁就旧客户端。");
    }
}
