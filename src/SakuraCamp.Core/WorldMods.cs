using System.Text;
using MoonSharp.Interpreter;

namespace SakuraCamp.Core;

public sealed record ModEntryState(bool Enabled, IReadOnlyDictionary<string, LuaValue> Options);
public sealed record ShardModFile(string Shard, string Path, byte[]? Original, IReadOnlyDictionary<string, ModEntryState> Entries)
{
    public ModEntryState Entry(string key) => Entries.GetValueOrDefault(key) ?? new(false, new Dictionary<string, LuaValue>());
}
public sealed record WorldModSnapshot(string WorldPath, IReadOnlyList<ShardModFile> Files)
{
    public IEnumerable<string> Keys => Files.SelectMany(f => f.Entries.Keys).Distinct(StringComparer.Ordinal);
    public bool AnyEnabled(string key) => Files.Any(f => f.Entry(key).Enabled);
    public bool AllEnabled(string key) => Files.Count > 0 && Files.All(f => f.Entry(key).Enabled);
}

public static class WorldMods
{
    private static string Text(byte[]? bytes) => bytes is null ? "return {}" : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    private static Table Parse(byte[]? bytes)
    {
        var (_, result) = ModLua.Read(Text(bytes));
        if (result.Type != DataType.Table) throw new InvalidOperationException("modoverrides.lua 必须返回配置表，已保留原文件。");
        return result.Table;
    }

    public static WorldModSnapshot Read(WorldInfo world)
    {
        var files = new List<ShardModFile>();
        foreach (var shard in world.HasCaves ? new[] { "Master", "Caves" } : new[] { "Master" })
        {
            var path = Path.Combine(world.Path, shard, "modoverrides.lua");
            var bytes = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var table = Parse(bytes);
            var entries = new Dictionary<string, ModEntryState>(StringComparer.Ordinal);
            foreach (var pair in table.Pairs)
            {
                if (pair.Key.Type != DataType.String || pair.Value.Type != DataType.Table) continue;
                var mod = pair.Value.Table;
                var options = new Dictionary<string, LuaValue>(StringComparer.Ordinal);
                var overrides = mod.Get("configuration_options");
                if (overrides.Type == DataType.Table)
                foreach (var option in overrides.Table.Pairs)
                    if (option.Key.Type == DataType.String) options[option.Key.String] = ModLua.Value(option.Value);
                entries[pair.Key.String] = new(mod.Get("enabled").CastToBool(), options);
            }
            files.Add(new(shard, path, bytes, entries));
        }
        return new(world.Path, files);
    }

    public static string Apply(WorldInfo world, WorldModSnapshot snapshot, InstalledMod mod, bool? enabled,
        IReadOnlyDictionary<string, LuaValue>? options, IReadOnlyCollection<string> shards, string backupRoot)
    {
        WorldFiles.RequireStopped();
        if (!Path.GetFullPath(world.Path).Equals(Path.GetFullPath(snapshot.WorldPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("世界已切换，请等待 Mod 列表自动更新后重试。");
        if (shards.Count == 0 || shards.Any(s => !snapshot.Files.Any(f => f.Shard == s))) throw new InvalidOperationException("无效的世界配置目标。");
        if (enabled == true && (!mod.Downloaded || mod.ClientOnly || mod.Error.Length > 0)) throw new InvalidOperationException("此 Mod 未完整读取、未下载或仅供客户端使用，无法启用。");
        if (options is not null)
        foreach (var (key, value) in options)
        {
            var definitions = mod.Options.Where(o => o.Name == key).ToArray();
            if (!definitions.Any(o => o.Choices.Any(c => c.Value == value) || o.Default == value))
                throw new InvalidOperationException("配置值不在此 Mod 提供的选项中：" + key);
        }
        var edits = new List<(ShardModFile File, string Text)>();
        foreach (var file in snapshot.Files.Where(f => shards.Contains(f.Shard)))
        {
            var table = Parse(file.Original);
            var value = table.Get(mod.Key);
            if (!value.IsNil() && value.Type != DataType.Table) throw new InvalidOperationException("此 Mod 的存档配置不是表，已保留原文件。");
            var entry = value.IsNil() ? new Table(table.OwnerScript) : value.Table;
            if (value.IsNil() && enabled is null) entry.Set("enabled", DynValue.NewBoolean(false));
            if (enabled is { } on) entry.Set("enabled", DynValue.NewBoolean(on));
            if (options is not null && options.Count > 0)
            {
                var config = entry.Get("configuration_options");
                if (!config.IsNil() && config.Type != DataType.Table) throw new InvalidOperationException("此 Mod 的选项不是配置表，已保留原文件。");
                var target = config.IsNil() ? new Table(table.OwnerScript) : config.Table;
                foreach (var (key, option) in options) target.Set(key, ModLua.CopyTo(table.OwnerScript, ModLua.Read("return " + option.Code).Result));
                entry.Set("configuration_options", DynValue.NewTable(target));
            }
            table.Set(mod.Key, DynValue.NewTable(entry));
            // Validate every value, including entries we don't edit, before changing either shard.
            _ = ModLua.Value(DynValue.NewTable(table));
            var lines = table.Pairs.Select(p => (Key: ModLua.Value(p.Key).Code, Value: ModLua.Value(p.Value).Code))
                .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"    [{p.Key}] = {p.Value},");
            edits.Add((file, "return {\n" + string.Join("\n", lines) + "\n}\n"));
        }
        CheckCurrent(snapshot);
        var backup = Path.Combine(backupRoot, "mod-configs", Path.GetFileName(world.Path) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(backup);
        foreach (var file in snapshot.Files)
        {
            var directory = Path.Combine(backup, file.Shard); Directory.CreateDirectory(directory);
            if (file.Original is not null) File.WriteAllBytes(Path.Combine(directory, "modoverrides.lua"), file.Original);
            else File.WriteAllText(Path.Combine(directory, "modoverrides.absent"), "Originally absent");
        }
        CheckCurrent(snapshot); WorldFiles.RequireStopped();
        var written = new List<ShardModFile>();
        try
        {
            foreach (var edit in edits) { AtomicFile.Write(edit.File.Path, edit.Text); written.Add(edit.File); }
        }
        catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var file in written)
                try { if (file.Original is null) File.Delete(file.Path); else File.WriteAllBytes(file.Path, file.Original); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { rollbackErrors.Add(rollbackError); }
            throw new InvalidOperationException((rollbackErrors.Count == 0 ? "保存失败，已恢复原配置。" : "保存失败且部分配置恢复失败，请从备份恢复：") + backup, writeError);
        }
        return backup;
    }

    public static void CheckCurrent(WorldModSnapshot snapshot)
    {
        foreach (var file in snapshot.Files)
        {
            var current = File.Exists(file.Path) ? File.ReadAllBytes(file.Path) : null;
            if ((current is null) != (file.Original is null) || current is not null && !current.AsSpan().SequenceEqual(file.Original))
                throw new InvalidOperationException("Mod 配置已被游戏或其他程序修改，请等待列表自动更新后重新打开配置，避免覆盖新设置。");
        }
    }

    public static string SyncList(IEnumerable<InstalledMod> mods, WorldModSnapshot snapshot)
    {
        var selected = mods.Where(m => snapshot.AnyEnabled(m.Key)).ToArray();
        return selected.Length == 0 ? "当前世界没有启用服务器 Mod。" :
            string.Join("\n\n", selected.Select((m, i) => $"{i + 1}. {m.Name}\n版本：{m.Version} · 作者：{m.Author}" +
                (m.Url.Length > 0 ? "\n" + m.Url : "\n本地 Mod：" + m.Key + "（需由房主另行提供文件）") +
                (m.AllClientsRequire ? "\n加入时需要客户端同步" : "\n服务器 Mod；客户端要求以游戏实际加载为准"))) +
            "\n\n订阅后等待 Steam 下载完成，再启动游戏加入。";
    }
}
