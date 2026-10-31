using System.Text;
using MoonSharp.Interpreter;

namespace SakuraCamp.Core;

public sealed record RuleFile(string Shard, string Path, byte[]? Original);
public sealed record ShardRules(string Shard, IReadOnlyDictionary<string, LuaValue> Values);
public sealed record WorldRuleSnapshot(string WorldPath, IReadOnlyList<RuleFile> Files, IReadOnlyList<ShardRules> Shards);

public static class WorldRules
{
    private static Table Parse(byte[]? bytes)
    {
        var (_, value) = ModLua.Read(bytes is null ? "return {}" : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
        if (value.Type != DataType.Table) throw new InvalidOperationException("世界规则文件必须返回设置表，原文件已保留。");
        var overrides = value.Table.Get("overrides");
        if (!overrides.IsNil() && overrides.Type != DataType.Table) throw new InvalidOperationException("世界规则 overrides 格式无效，原文件已保留。");
        return value.Table;
    }

    public static WorldRuleSnapshot Read(WorldInfo world)
    {
        var files = new List<RuleFile>(); var shards = new List<ShardRules>();
        foreach (var shard in world.HasCaves ? new[] { "Master", "Caves" } : new[] { "Master" })
        {
            var values = new Dictionary<string, LuaValue>();
            foreach (var name in new[] { "leveldataoverride.lua", "worldgenoverride.lua" })
            {
                var path = Path.Combine(world.Path, shard, name);
                var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
                files.Add(new(shard, path, bytes));
                if (bytes is null) continue;
                var table = Parse(bytes);
                if (name == "worldgenoverride.lua")
                {
                    if (!table.Get("override_enabled").CastToBool()) continue;
                    if (!table.Get("preset").IsNil() || !table.Get("settings_preset").IsNil())
                        throw new InvalidOperationException("此世界使用规则预设覆盖，请先在游戏中保存为完整世界设置，再编辑规则，以免显示与实际预设不一致。");
                }
                if (table.Get("overrides").Table is { } overrides)
                    foreach (var entry in overrides.Pairs)
                        if (entry.Key.Type == DataType.String) values[entry.Key.String] = ModLua.Value(entry.Value);
            }
            shards.Add(new(shard, values));
        }
        return new(world.Path, files, shards);
    }

    public static string Apply(WorldInfo world, WorldRuleSnapshot snapshot, IReadOnlyList<WorldRuleDefinition> catalog,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, LuaValue>> changes, string backupRoot)
    {
        WorldFiles.RequireStopped();
        if (!Path.GetFullPath(world.Path).Equals(Path.GetFullPath(snapshot.WorldPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("世界已切换，请重新读取世界规则。");
        if (changes.Count == 0 || changes.Values.All(v => v.Count == 0)) throw new InvalidOperationException("没有需要保存的世界规则修改。");
        var edits = new List<(RuleFile File, string Source)>();
        foreach (var (shard, entries) in changes)
        {
            if (shard is not ("Master" or "Caves") || !snapshot.Shards.Any(s => s.Shard == shard) || shard == "Caves" && !world.HasCaves)
                throw new InvalidOperationException("无效的世界规则目标。");
            if (entries.Count == 0) continue;
            var file = snapshot.Files.Single(f => f.Shard == shard && Path.GetFileName(f.Path) == "worldgenoverride.lua");
            if (!Path.GetFullPath(file.Path).Equals(Path.GetFullPath(Path.Combine(world.Path, shard, "worldgenoverride.lua")), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("世界规则文件路径不属于当前世界。");
            var table = Parse(file.Original);
            if (!table.Get("preset").IsNil() || !table.Get("settings_preset").IsNil())
                throw new InvalidOperationException("现有覆盖文件包含规则预设，无法直接启用或改写，以免同时改变未选择的规则。原文件未修改。");
            var options = table.Get("overrides").Table ?? new Table(table.OwnerScript);
            if (file.Original is not null && !table.Get("override_enabled").CastToBool() && options.Pairs.Any())
                throw new InvalidOperationException("已有规则覆盖文件处于禁用状态且含旧设置，无法直接启用以免误用旧规则。请先核对该文件，原文件未修改。");
            foreach (var (key, value) in entries)
            {
                var definition = catalog.SingleOrDefault(r => r.Key == key);
                if (definition is null || !definition.Available(shard) || shard != "Master" && definition.MasterControlled || !definition.Choices.Any(c => c.Value == value))
                    throw new InvalidOperationException("此世界规则或取值不可用：" + key);
                options.Set(key, ModLua.CopyTo(table.OwnerScript, ModLua.Read("return " + value.Code).Result));
            }
            table.Set("overrides", DynValue.NewTable(options));
            table.Set("override_enabled", DynValue.NewBoolean(true));
            // No preset is added: partial overrides layer on the game's existing level data.
            edits.Add((file, "return " + ModLua.Value(DynValue.NewTable(table)).Code + "\n"));
        }
        CheckCurrent(snapshot);
        var backup = Path.Combine(backupRoot, "world-rules", world.Folder + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        foreach (var file in snapshot.Files)
        {
            var folder = Path.Combine(backup, file.Shard); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, Path.GetFileName(file.Path) + (file.Original is null ? ".absent" : "")), file.Original ?? []);
        }
        CheckCurrent(snapshot); WorldFiles.RequireStopped();
        var written = new List<RuleFile>();
        try
        {
            foreach (var edit in edits) { AtomicFile.Write(edit.File.Path, edit.Source); written.Add(edit.File); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var errors = new List<Exception>();
            foreach (var file in written)
                try { if (file.Original is null) File.Delete(file.Path); else File.WriteAllBytes(file.Path, file.Original); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException) { errors.Add(rollback); }
            throw new InvalidOperationException(errors.Count == 0 ? "保存失败，已恢复原规则。备份：" + backup : "保存失败，部分规则无法恢复，请从备份恢复：" + backup, ex);
        }
        return backup;
    }

    public static void CheckCurrent(WorldRuleSnapshot snapshot)
    {
        foreach (var file in snapshot.Files)
        {
            var bytes = File.Exists(file.Path) ? File.ReadAllBytes(file.Path) : null;
            if ((bytes is null) != (file.Original is null) || bytes is not null && !bytes.AsSpan().SequenceEqual(file.Original))
                throw new InvalidOperationException("世界规则已被其他程序修改，请重新读取后再保存，避免覆盖新设置。");
        }
    }
}
