using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MoonSharp.Interpreter;

namespace SakuraCamp.Core;

public sealed record WorldRuleChoice(string Label, LuaValue Value);
public sealed record WorldRuleDefinition(string Key, string Name, string Group, int GroupOrder, int Order,
    string[] Locations, bool MasterControlled, LuaValue Default, IReadOnlyList<WorldRuleChoice> Choices)
{
    public string IconAtlas { get; init; } = "";
    public string IconName { get; init; } = "";
    public bool Available(string shard) => Locations.Length == 0 || Locations.Contains(shard == "Master" ? "forest" : "cave");
}

public static class WorldRuleCatalog
{
    public static IReadOnlyList<WorldRuleDefinition> Load(string serverExe)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(serverExe))!;
        var path = new[] { Path.Combine(folder, "..", "data", "databundles", "scripts.zip"), Path.Combine(folder, "data", "databundles", "scripts.zip") }
            .FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("未找到游戏的规则资源，请在文件目录中选择已安装的饥荒服务器程序。");
        using var archive = ZipFile.OpenRead(path);
        string Read(string name, int limit)
        {
            var entry = archive.GetEntry("scripts/" + name) ?? archive.GetEntry(name)
                ?? throw new InvalidOperationException("游戏资源缺少世界规则定义：" + name);
            if (entry.Length > limit) throw new InvalidOperationException("游戏规则资源过大，已停止读取。");
            using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd();
        }
        var translations = ParseTranslations(Read("languages/chinese_s.po", 32 * 1024 * 1024));
        return Parse(Read("map/customize.lua", 2 * 1024 * 1024), translations);
    }

    public static IReadOnlyDictionary<string, string> ParseTranslations(string po)
    {
        var values = new Dictionary<string, string>();
        string context = "", id = "", translated = ""; var field = "";
        void Commit()
        {
            if (context.StartsWith("STRINGS.UI.SANDBOXMENU.", StringComparison.Ordinal) || context.StartsWith("STRINGS.UI.CUSTOMIZATIONSCREEN.", StringComparison.Ordinal))
                values[context] = translated.Length > 0 ? translated : id;
            context = id = translated = field = "";
        }
        foreach (var raw in po.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { Commit(); continue; }
            if (line.StartsWith('#')) continue;
            if (line.StartsWith("msgctxt ")) { if (context.Length > 0) Commit(); field = "context"; }
            else if (line.StartsWith("msgid ")) field = "id";
            else if (line.StartsWith("msgstr ")) field = "translated";
            else if (!line.StartsWith('"')) { field = ""; continue; }
            var quote = line.IndexOf('"'); if (quote < 0) continue;
            string value;
            try { value = JsonSerializer.Deserialize<string>(line[quote..]) ?? ""; }
            catch (JsonException) { continue; }
            switch (field) { case "context": context += value; break; case "id": id += value; break; case "translated": translated += value; break; }
        }
        Commit(); return values;
    }

    public static IReadOnlyList<WorldRuleDefinition> Parse(string source, IReadOnlyDictionary<string, string> translations)
    {
        var start = source.IndexOf("local WORLDSETTINGS_GROUP =", StringComparison.Ordinal);
        var end = source.IndexOf("local WORLDSETTINGS_MISC", start < 0 ? 0 : start, StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidOperationException("游戏世界规则格式已变化，未修改任何设置。");
        // Read the game's data tables only. Stub map-generation dependencies; never execute
        // the rest of the game, import modules, or grant file/network access to its Lua.
        var data = source[..end];
        data = Regex.Replace(data, @"^local \w+ = require\s*\([^\r\n]+", "", RegexOptions.Multiline);
        data = "local tasksets={} local tasks={} local startlocations={} local Levels={} " + data + "\nreturn WORLDSETTINGS_GROUP";
        var (_, result) = ModLua.Read(data, configure: script =>
        {
            var strings = new Table(script); script.Globals.Set("STRINGS", DynValue.NewTable(strings));
            foreach (var key in Regex.Matches(data, @"STRINGS(?:\.[A-Z0-9_]+)+").Select(m => m.Value).Distinct())
            {
                var parts = key.Split('.').Skip(1).ToArray(); var target = strings;
                foreach (var part in parts[..^1])
                {
                    if (target.Get(part).Type != DataType.Table) target.Set(part, DynValue.NewTable(new Table(script)));
                    target = target.Get(part).Table;
                }
                target.Set(parts[^1], DynValue.NewString(translations.GetValueOrDefault(key) ?? parts[^1]));
            }
            script.Globals["IsNotConsole"] = (Func<bool>)(() => true);
            script.Globals["IsPS4"] = (Func<bool>)(() => false);
        });
        if (result.Type != DataType.Table) throw new InvalidOperationException("无法读取游戏世界规则。");
        var rules = new List<WorldRuleDefinition>();
        foreach (var groupPair in result.Table.Pairs)
        {
            var group = groupPair.Value.Table; if (group is null) continue;
            var items = group.Get("items").Table; if (items is null) continue;
            foreach (var pair in items.Pairs)
            {
                var key = pair.Key.String; var item = pair.Value.Table; if (key is null || item is null) continue;
                var choices = item.Get("desc"); if (choices.IsNil()) choices = group.Get("desc");
                if (choices.Type != DataType.Table) throw new InvalidOperationException("世界规则选项无法解析：" + key);
                var options = choices.Table.Values.Select(v => v.Table).Where(t => t is not null)
                    .Select(t => new WorldRuleChoice(t.Get("text").String ?? "", ModLua.Value(t.Get("data")))).ToArray();
                if (options.Length == 0) throw new InvalidOperationException("世界规则缺少可选值：" + key);
                var name = translations.GetValueOrDefault("STRINGS.UI.CUSTOMIZATIONSCREEN." + key.ToUpperInvariant()) ?? key;
                rules.Add(new(key, name, group.Get("text").String ?? groupPair.Key.String, (int)group.Get("order").Number,
                    item.Get("order").IsNil() ? 1000 : (int)item.Get("order").Number,
                    item.Get("world").Table?.Values.Select(v => v.String).ToArray() ?? [], item.Get("master_controlled").CastToBool(),
                    ModLua.Value(item.Get("value")), options)
                { IconAtlas = item.Get("atlas").String ?? group.Get("atlas").String ?? "", IconName = item.Get("image").String ?? "" });
            }
        }
        if (rules.Count < 10) throw new InvalidOperationException("游戏规则列表不完整，未修改任何设置。");
        return rules.OrderBy(r => r.GroupOrder).ThenBy(r => r.Order).ThenBy(r => r.Name, StringComparer.CurrentCulture).ToArray();
    }
}
