using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace SakuraCamp.Core;

public sealed record ModChoice(string Label, LuaValue Value, string Hover = "");
public sealed record ModOption(string Name, string Label, string Hover, LuaValue Default, IReadOnlyList<ModChoice> Choices)
{
    public bool IsHeading => string.IsNullOrEmpty(Name) || Choices.Count == 0 || Choices.All(c => string.IsNullOrWhiteSpace(c.Label));
}
public sealed record InstalledMod(string Key, string Name, string Version, string Author, string Description,
    string Folder, bool ClientOnly, bool AllClientsRequire, string IconAtlas, string Icon,
    IReadOnlyList<ModOption> Options, string Error)
{
    public string WorkshopId => Key.StartsWith("workshop-", StringComparison.Ordinal) ? Key[9..] : "";
    public string Url => Regex.IsMatch(WorkshopId, @"^\d+$") ? "https://steamcommunity.com/sharedfiles/filedetails/?id=" + WorkshopId : "";
    public bool Downloaded => File.Exists(Path.Combine(Folder, "modinfo.lua"));
}

public static class ModCatalog
{
    public static IReadOnlyList<InstalledMod> Scan(string workshop, string serverExe, IEnumerable<string>? configured = null)
    {
        var folders = FindFolders(workshop, serverExe);
        foreach (var key in configured ?? []) folders.TryAdd(key, "");
        return ReadFolders(folders);
    }

    // Reads only directory entries and metadata, without evaluating Lua on every poll.
    public static string Fingerprint(string workshop, string serverExe)
    {
        var text = new System.Text.StringBuilder(workshop + "\n" + serverExe + "\n");
        foreach (var (key, folder) in FindFolders(workshop, serverExe).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            text.Append(key).Append('|').Append(folder).AppendLine();
            foreach (var path in Directory.GetFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not (".lua" or ".xml" or ".tex" or ".png" or ".jpg" or ".jpeg")) continue;
                var info = new FileInfo(path);
                text.Append(path).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).AppendLine();
            }
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static Dictionary<string, string> FindFolders(string workshop, string serverExe)
    {
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var game = Path.GetDirectoryName(Path.GetDirectoryName(serverExe)) ?? "";
        AddRoot(Path.Combine(workshop, "content", "322330"), true);
        AddRoot(Path.Combine(workshop, "content", "343050"), true);
        AddRoot(Path.Combine(game, "mods"), false);
        var ugc = Path.Combine(game, "ugc_mods");
        if (Directory.Exists(ugc)) foreach (var path in Directory.GetDirectories(ugc)) AddRoot(Path.Combine(path, "content", "322330"), true);
        return folders;

        void AddRoot(string root, bool numeric)
        {
            if (!Directory.Exists(root)) return;
            foreach (var path in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(path, "modinfo.lua"))) continue;
                var name = Path.GetFileName(path);
                if (numeric && !Regex.IsMatch(name, @"^\d+$")) continue;
                var key = numeric ? "workshop-" + name : name;
                folders.TryAdd(key, path);
            }
        }
    }

    private static IReadOnlyList<InstalledMod> ReadFolders(Dictionary<string, string> folders)
    {
        var result = new List<InstalledMod>();
        foreach (var (key, folder) in folders)
        {
            var empty = new InstalledMod(key, key, "—", "未知作者", "", folder, false, true, "", "", [], "");
            if (folder.Length == 0) { result.Add(empty with { Error = "存档中有配置，但本地未找到 Mod 文件" }); continue; }
            Script? script = null;
            try
            {
                if (new FileInfo(Path.Combine(folder, "modinfo.lua")).Length > 2 * 1024 * 1024) throw new InvalidOperationException("modinfo.lua 过大，无法读取。");
                (script, _) = ModLua.Read(File.ReadAllText(Path.Combine(folder, "modinfo.lua")), key, metadata: true);
                var globals = script.Globals;
                string Text(string name, string fallback = "") { var v = globals.Get(name); return v.Type == DataType.String ? v.String.Trim() : v.Type == DataType.Number ? v.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : fallback; }
                var model = empty with
                {
                    Name = Text("name", key), Version = Text("version", "—"), Author = Text("author", "未知作者"), Description = Text("description"),
                    ClientOnly = globals.Get("client_only_mod").CastToBool(), AllClientsRequire = globals.Get("all_clients_require_mod").CastToBool(),
                    IconAtlas = Text("icon_atlas"), Icon = Text("icon")
                };
                try { model = model with { Options = ReadOptions(globals.Get("configuration_options")) }; }
                catch (InvalidOperationException ex) { model = model with { Error = ex.Message }; }
                result.Add(model);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // Partial metadata may still identify a client-only mod; never enable an unreadable mod.
                var globals = script?.Globals;
                result.Add(empty with { ClientOnly = globals?.Get("client_only_mod").CastToBool() == true, Error = ex.Message });
            }
        }
        return result.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    }

    private static IReadOnlyList<ModOption> ReadOptions(DynValue value)
    {
        if (value.IsNil()) return [];
        if (value.Type != DataType.Table) throw new InvalidOperationException("configuration_options 不是配置表。");
        var result = new List<ModOption>();
        foreach (var pair in value.Table.Pairs.OrderBy(p => p.Key.Number))
        {
            if (pair.Value.Type != DataType.Table) continue;
            if (result.Count >= 1500) throw new InvalidOperationException("Mod 配置项过多。");
            var row = pair.Value.Table;
            string Text(string name) => row.Get(name).Type == DataType.String ? row.Get(name).String : "";
            var choices = new List<ModChoice>();
            if (row.Get("options").Type == DataType.Table)
            foreach (var option in row.Get("options").Table.Pairs.OrderBy(p => p.Key.Number))
            {
                if (option.Value.Type != DataType.Table) continue;
                if (choices.Count >= 3000) throw new InvalidOperationException("Mod 单项可选值过多。");
                var t = option.Value.Table; var data = ModLua.Value(t.Get("data"));
                var description = t.Get("description"); var hover = t.Get("hover");
                choices.Add(new(description.Type == DataType.String ? description.String : data.Code, data, hover.Type == DataType.String ? hover.String : ""));
            }
            var name = Text("name"); var label = Text("label");
            result.Add(new(name, label.Length == 0 ? name : label, Text("hover"), ModLua.Value(row.Get("default")), choices));
        }
        return result;
    }
}
