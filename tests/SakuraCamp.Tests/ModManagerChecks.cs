using System.Diagnostics;
using System.Text;
using SakuraCamp.Core;

static class ModManagerChecks
{
    public static void Run(Action<bool, string> check, string testRoot)
    {
        var root = Path.Combine(testRoot, "mod-manager");
        var workshop = Path.Combine(root, "workshop");
        var game = Path.Combine(root, "game");
        var exe = Path.Combine(game, "bin64", "dontstarve_dedicated_server_nullrenderer_x64.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllText(exe, "fixture");
        string Mod(string id, string text)
        {
            var folder = Path.Combine(workshop, "content", "322330", id); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "modinfo.lua"), text, new UTF8Encoding(false)); return folder;
        }
        var primaryFolder = Mod("1001", """
            local title=ChooseTranslationTable({"English", zh="测试服务器 Mod"})
            name=title version="2.7" author="作者🌸" description=[[文字 -- 不是注释
            多行描述]] client_only_mod=false all_clients_require_mod=true
            icon_atlas="preview.xml" icon="preview.tex"
            local numbers={} for i=0,3 do numbers[#numbers+1]={description="数量"..i,data=i} end
            local function flag(name) return {name=name,label="开关",hover="说明",options={{description="开",data=true},{description="关",data=false}},default=false} end
            configuration_options={flag("flag"),{name="amount",label="数量",options=numbers,default=0},
            {name="text",label="文本",options={{description="带引号的文字",data="a\"b\\c\n中文"},{description="普通",data="zero"}},default="zero"},
            {name="table",label="表值",options={{description="组合",data={a=false,b=0,[3]="三"}}},default={a=false,b=0,[3]="三"}}}
            """);
        Mod("1002", "name='客户端' version='1' client_only_mod=true");
        Mod("1003", "name='未在存档中使用的服务器 Mod' server_only_mod=true version='3'");
        Mod("1004", "name='循环' while true do end");
        var marker = Path.Combine(root, "must-not-be-written.txt");
        Mod("1005", "io.open(" + WorldFiles.Lua(marker.Replace('\\', '/')) + ",'w'):write('forbidden')");
        var localFolder = Path.Combine(game, "mods", "my-local-mod"); Directory.CreateDirectory(localFolder);
        File.WriteAllText(Path.Combine(localFolder, "modinfo.lua"), "name='本地服务器Mod' server_only_mod=true author='Local'");
        var duplicate = Path.Combine(game, "mods", "workshop-1001"); Directory.CreateDirectory(duplicate);
        File.WriteAllText(Path.Combine(duplicate, "modinfo.lua"), "name='wrong duplicate'");
        var clock = Stopwatch.StartNew();
        var catalog = ModCatalog.Scan(workshop, exe, ["workshop-9999"]);
        var primary = catalog.Single(m => m.Key == "workshop-1001");
        check(primary.Name == "测试服务器 Mod" && primary.Author == "作者🌸" && primary.Version == "2.7" && primary.Folder == primaryFolder, "localized metadata, authors and versions resolve without duplicate workshop entries");
        check(primary.Description.Contains("-- 不是注释") && primary.Description.Contains("多行描述"), "Lua multiline text and comment-like characters survive metadata reading");
        check(primary.Options.Count == 4 && primary.Options[1].Choices.Count == 4 && primary.Options[0].Default.Code == "false", "generated configuration options preserve false and numeric zero defaults");
        check(primary.Options[3].Choices.Single().Value == primary.Options[3].Default, "table-valued defaults compare structurally with selectable data");
        check(catalog.Single(m => m.Key == "workshop-1002").ClientOnly && catalog.Any(m => m.Key == "workshop-1003") && catalog.Any(m => m.Key == "my-local-mod"), "catalog distinguishes client mods and includes unconfigured downloaded and local server mods");
        check(!catalog.Single(m => m.Key == "workshop-9999").Downloaded, "configured but missing mod remains visible for diagnosis");
        check(clock.Elapsed < TimeSpan.FromSeconds(5) && catalog.Single(m => m.Key == "workshop-1004").Error.Contains("限额"), "infinite metadata loop is interrupted instead of hanging the UI");
        check(!File.Exists(marker) && catalog.Single(m => m.Key == "workshop-1005").Error.Length > 0, "metadata sandbox exposes no filesystem access");
        var signature = ModCatalog.Fingerprint(workshop, exe);
        check(signature == ModCatalog.Fingerprint(workshop, exe), "unchanged Mod trees have a stable lightweight fingerprint");
        var added = Mod("1006", "name='自动发现' version='1'");
        var addedSignature = ModCatalog.Fingerprint(workshop, exe);
        check(signature != addedSignature && ModCatalog.Scan(workshop, exe).Any(m => m.Key == "workshop-1006"), "new downloaded server Mod changes fingerprint and appears without world configuration");
        File.WriteAllText(Path.Combine(added, "preview.png"), "fixture");
        check(addedSignature != ModCatalog.Fingerprint(workshop, exe), "downloaded preview completion invalidates the catalog fingerprint");
        Directory.Delete(added, recursive: true);
        check(signature == ModCatalog.Fingerprint(workshop, exe) && !ModCatalog.Scan(workshop, exe).Any(m => m.Key == "workshop-1006"), "deleted unconfigured server Mod disappears from catalog");
        check(ModCatalog.Scan(workshop, exe, ["workshop-1006"]).Single(m => m.Key == "workshop-1006").Downloaded == false, "deleted configured Mod retains a missing-files entry");
        var emptyWorkshop = Path.Combine(root, "not-yet-downloaded");
        var emptySignature = ModCatalog.Fingerprint(emptyWorkshop, exe);
        var lateFolder = Path.Combine(emptyWorkshop, "content", "343050", "2001"); Directory.CreateDirectory(lateFolder);
        File.WriteAllText(Path.Combine(lateFolder, "modinfo.lua"), "name='Late server mod'");
        check(emptySignature != ModCatalog.Fingerprint(emptyWorkshop, exe), "a previously absent workshop root is detected after Steam creates it");
        var ugcFolder = Path.Combine(game, "ugc_mods", "fixture", "content", "322330", "3001"); Directory.CreateDirectory(ugcFolder);
        File.WriteAllText(Path.Combine(ugcFolder, "modinfo.lua"), "name='UGC server mod'");
        check(ModCatalog.Scan(workshop, exe).Any(m => m.Key == "workshop-3001"), "UGC server downloads share discovery and fingerprint roots");
        if (Process.GetProcessesByName("dontstarve_dedicated_server_nullrenderer_x64").Length > 0)
        { Console.WriteLine("SKIP Mod save checks: real game server is running."); return; }

        var cluster = Path.Combine(root, "Cluster_Mods");
        Directory.CreateDirectory(cluster);
        File.WriteAllText(Path.Combine(cluster, "cluster.ini"), "[NETWORK]\ncluster_name=Mod fixture\n");
        foreach (var shard in new[] { "Master", "Caves" })
        {
            Directory.CreateDirectory(Path.Combine(cluster, shard));
            File.WriteAllText(Path.Combine(cluster, shard, "server.ini"), "[NETWORK]\nserver_port=12345");
            var amount = shard == "Master" ? 1 : 2;
            File.WriteAllText(Path.Combine(cluster, shard, "modoverrides.lua"), """
                -- Original comment; preserve its exact bytes in the backup.
                return {
                  ["workshop-1001"]={enabled=false,custom_field={a=false,n=0},configuration_options={amount=__AMOUNT__,flag=false,removed_option="do not drop"}},
                  ["workshop-8888"]={enabled=true,configuration_options={s="literal -- text",nested={enabled=false,[2]="two"}}},
                  ["workshop-1002"]={enabled=true},
                  metadata="unknown root field"
                }
                """.Replace("__AMOUNT__", amount.ToString()), new UTF8Encoding(true));
        }
        var world = Discovery.ReadWorld(cluster);
        var before = WorldMods.Read(world);
        var originals = before.Files.ToDictionary(f => f.Shard, f => f.Original!);
        check(!before.AnyEnabled(primary.Key) && before.Files[0].Entry(primary.Key).Options["flag"].Code == "false", "world Mod reading preserves disabled configurations and explicit false");
        string Apply(WorldModSnapshot state, bool? enabled, Dictionary<string, LuaValue>? options, params string[] shards) =>
            WorldMods.Apply(world, state, primary, enabled, options, shards, Path.Combine(root, "backups"));
        var backup = Apply(before, true, null, "Master", "Caves");
        var after = WorldMods.Read(world);
        check(after.AllEnabled(primary.Key) && after.Files.Select(f => f.Entry(primary.Key).Options["amount"].Code).SequenceEqual(["1", "2"]), "enable affects both shards without flattening different per-shard options");
        check(after.Files.All(f => f.Entry("workshop-8888").Enabled && f.Entry("workshop-8888").Options["s"].Code == "\"literal -- text\"" && f.Entry(primary.Key).Options["removed_option"].Code == "\"do not drop\""), "saving preserves unknown mods, old options and strings containing comment syntax");
        check(originals.All(pair => File.ReadAllBytes(Path.Combine(backup, pair.Key, "modoverrides.lua")).SequenceEqual(pair.Value)), "both original configuration files are backed up byte for byte before edits");
        check(File.ReadAllText(after.Files[0].Path).Contains("[\"metadata\"]=") || File.ReadAllText(after.Files[0].Path).Contains("[\"metadata\"] ="), "unknown root fields survive serialization");
        var caveBytes = File.ReadAllBytes(after.Files[1].Path);
        Apply(after, null, new() { ["flag"] = new("true"), ["amount"] = new("0") }, "Master");
        var configured = WorldMods.Read(world);
        check(configured.Files[0].Entry(primary.Key).Options["amount"].Code == "0" && configured.Files[0].Entry(primary.Key).Options["flag"].Code == "true" && File.ReadAllBytes(configured.Files[1].Path).SequenceEqual(caveBytes), "single-shard configuration changes leave the other shard byte-identical");
        var textChoice = primary.Options.Single(o => o.Name == "text").Choices[0].Value;
        Apply(configured, null, new() { ["text"] = textChoice, ["table"] = primary.Options[3].Default }, "Master", "Caves");
        var typed = WorldMods.Read(world);
        check(typed.Files.All(f => f.Entry(primary.Key).Options["text"] == textChoice && f.Entry(primary.Key).Options["table"] == primary.Options[3].Default), "escaped text and nested table option values round-trip without changing types");
        Apply(typed, false, null, "Master", "Caves"); var disabled = WorldMods.Read(world);
        check(!disabled.AnyEnabled(primary.Key) && disabled.Files[0].Entry(primary.Key).Options["text"] == textChoice, "disable retains options for later re-enabling");
        var list = WorldMods.SyncList(catalog.Where(m => !m.ClientOnly), disabled);
        check(!list.Contains(primary.Name) && !list.Contains("客户端"), "friend synchronization includes enabled server mods only");
        Apply(disabled, true, null, "Master", "Caves");
        var enabledList = WorldMods.SyncList(catalog.Where(m => !m.ClientOnly), WorldMods.Read(world));
        check(enabledList.Contains(primary.Name) && enabledList.Contains(primary.Url) && enabledList.Contains(primary.Version), "friend list retains names, versions and workshop links");

        void Reject(Action action, string title)
        {
            var state = WorldMods.Read(world).Files.ToDictionary(f => f.Path, f => File.ReadAllBytes(f.Path));
            try { action(); throw new Exception("Expected rejection: " + title); }
            catch (InvalidOperationException) { check(state.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), title); }
        }
        Reject(() => Apply(before, false, null, "Master", "Caves"), "stale snapshots cannot overwrite newer game or tool changes");
        Reject(() => Apply(WorldMods.Read(world), null, new() { ["amount"] = new("99") }, "Master"), "out-of-range values are rejected without writes");
        Reject(() => Apply(WorldMods.Read(world), null, new() { ["flag"] = new("os.execute('bad')") }, "Master"), "configuration injection is rejected before Lua evaluation");
        foreach (var key in new[] { "workshop-1002", "workshop-9999", "workshop-1004" })
            Reject(() => WorldMods.Apply(world, WorldMods.Read(world), catalog.Single(m => m.Key == key), true, null, ["Master"], Path.Combine(root, "backups")), "client-only, missing or unreadable mod cannot be enabled: " + key);

        var rollbackSnapshot = WorldMods.Read(world); var rollbackOriginal = rollbackSnapshot.Files.ToDictionary(f => f.Path, f => f.Original!);
        File.SetAttributes(rollbackSnapshot.Files[1].Path, FileAttributes.ReadOnly);
        try
        {
            try { Apply(rollbackSnapshot, false, null, "Master", "Caves"); throw new Exception("expected read-only cave failure"); }
            catch (InvalidOperationException) { check(rollbackOriginal.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "second-shard write failure rolls the first shard back to its original bytes"); }
        }
        finally { File.SetAttributes(rollbackSnapshot.Files[1].Path, FileAttributes.Normal); }
        File.AppendAllText(rollbackSnapshot.Files[1].Path, "\nerror('unreadable original')");
        try { WorldMods.Read(world); throw new Exception("expected malformed source rejection"); }
        catch (InvalidOperationException) { check(true, "invalid original Lua is rejected instead of replaced by an empty mod list"); }

        var soloPath = Path.Combine(root, "Cluster_Solo"); Directory.CreateDirectory(Path.Combine(soloPath, "Master"));
        File.WriteAllText(Path.Combine(soloPath, "cluster.ini"), "[NETWORK]\ncluster_name=Solo\n");
        var solo = Discovery.ReadWorld(soloPath); var soloBefore = WorldMods.Read(solo);
        var soloBackup = WorldMods.Apply(solo, soloBefore, primary, true, null, ["Master"], Path.Combine(root, "backups"));
        check(WorldMods.Read(solo).AllEnabled(primary.Key) && !Directory.Exists(Path.Combine(soloPath, "Caves")) && File.Exists(Path.Combine(soloBackup, "Master", "modoverrides.absent")), "surface-only world creates only its missing master config and records absence in backup");
        var unused = catalog.Single(m => m.Key == "workshop-1003") with { Options = primary.Options };
        WorldMods.Apply(solo, WorldMods.Read(solo), unused, null, new Dictionary<string, LuaValue> { ["flag"] = new("false") }, ["Master"], Path.Combine(root, "backups"));
        check(!WorldMods.Read(solo).AnyEnabled(unused.Key) && WorldMods.Read(solo).Files[0].Entry(unused.Key).Options["flag"].Code == "false", "configuring an unused mod saves options without silently enabling it");
    }
}
