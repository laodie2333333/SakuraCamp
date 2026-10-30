using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace SakuraCamp.Core;

public static class Discovery
{
    public static string DefaultSaveRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Klei", "DoNotStarveTogether");
    public static IReadOnlyList<WorldInfo> Worlds(string root)
    {
        var result = new List<WorldInfo>();
        if (!Directory.Exists(root)) return result;
        // Current folder, account/Cluster_N and older account-free layouts.
        var candidates = new List<string> { root };
        foreach (var child in SafeDirs(root)) { candidates.Add(child); candidates.AddRange(SafeDirs(child)); }
        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(path, "cluster.ini")) || !Directory.Exists(Path.Combine(path, "Master"))) continue;
            try { result.Add(ReadWorld(path)); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidOperationException) { }
        }
        return result.OrderByDescending(w => w.Modified).ToList();
    }
    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).ToArray(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
    public static WorldInfo ReadWorld(string path)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var file = Path.Combine(path, "cluster.ini");
        var ini = IniDocument.Load(file);
        return new(path, ini.Get("NETWORK", "cluster_name", Path.GetFileName(path)), ini.Get("GAMEPLAY", "game_mode", "survival"),
            int.TryParse(ini.Get("GAMEPLAY", "max_players"), out var max) ? max : 6,
            File.Exists(Path.Combine(path, "Caves", "server.ini")), File.Exists(Path.Combine(path, "cluster_token.txt")),
            ini.Get("NETWORK", "cluster_password").Length > 0, File.GetLastWriteTime(file));
    }

    [SupportedOSPlatform("windows")]
    public static (string Server, string Workshop, string Frpc) Installation()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)?.ToString();
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(steam)) libraries.Add(steam);
        foreach (var program in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (Directory.Exists(Path.Combine(program, "Steam"))) libraries.Add(Path.Combine(program, "Steam"));
        foreach (var lib in libraries.ToArray())
        {
            var file = Path.Combine(lib, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(file)) continue;
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "\"path\"\\s*\"([^\"]+)\""))
                libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
        }
        string server = "", workshop = "";
        foreach (var lib in libraries)
        {
            foreach (var folder in new[] { "Don't Starve Together Dedicated Server", "Don't Starve Together" })
            {
                var candidate = Path.Combine(lib, "steamapps", "common", folder, "bin64", "dontstarve_dedicated_server_nullrenderer_x64.exe");
                if (!File.Exists(candidate) || server.Length > 0) continue;
                server = candidate;
                workshop = Path.Combine(lib, "steamapps", "workshop");
            }
        }
        var standalone = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SakuraCamp", "tools", "frpc.exe");
        var frpc = File.Exists(standalone) ? standalone : "";
        return (server, workshop, frpc);
    }
    public static IReadOnlyList<ModInfo> Mods(WorldInfo world, string workshop, string serverExe)
    {
        var ids = new HashSet<string>();
        foreach (var shard in new[] { "Master", "Caves" })
        {
            var config = Path.Combine(world.Path, shard, "modoverrides.lua");
            if (!File.Exists(config)) continue;
            // Read metadata only. Never execute third-party Lua.
            var lua = Regex.Replace(File.ReadAllText(config), @"--\[\[[\s\S]*?\]\]|--[^\r\n]*", "");
            foreach (Match m in Regex.Matches(lua, "\\[\\s*[\"']workshop-(\\d+)[\"']\\s*\\]\\s*=\\s*\\{")) ids.Add(m.Groups[1].Value);
        }
        var mods = new List<ModInfo>();
        var gameDir = Path.GetDirectoryName(Path.GetDirectoryName(serverExe)) ?? "";
        foreach (var id in ids)
        {
            var roots = new[] { Path.Combine(workshop, "content", "322330", id), Path.Combine(gameDir, "mods", "workshop-" + id) };
            var folder = roots.FirstOrDefault(p => File.Exists(Path.Combine(p, "modinfo.lua")));
            var name = $"创意工坊 Mod {id}"; var version = "—";
            if (folder is not null)
            {
                var text = File.ReadAllText(Path.Combine(folder, "modinfo.lua"));
                var nameMatch = Regex.Match(text, "(?m)^name\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']");
                var versionMatch = Regex.Match(text, "(?m)^version\\s*=\\s*[\"']([^\"'\\r\\n]+)[\"']");
                if (nameMatch.Success) name = nameMatch.Groups[1].Value;
                if (versionMatch.Success) version = versionMatch.Groups[1].Value;
                if (name.Contains('(') || name.StartsWith("创意工坊", StringComparison.Ordinal))
                {
                    var log = Path.Combine(world.Path, "Master", "server_log.txt");
                    if (File.Exists(log))
                    {
                        var m = Regex.Match(LogReader.Tail(log, 1024 * 1024), @"Loading mod: workshop-" + id + @" \((.+?)\) Version:");
                        if (m.Success) name = m.Groups[1].Value;
                    }
                }
            }
            mods.Add(new(id, name, version, folder is not null));
        }
        return mods;
    }
}

public sealed class SettingsStore(string root)
{
    public string Root { get; } = root;
    public string BackupRoot => Path.Combine(Root, "backups");
    public AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }
    public void Save(AppSettings settings) => AtomicFile.Write(Path.Combine(Root, "settings.json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
}
