using SakuraCamp.Core;

static class WorldRuleChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "rules"); Directory.CreateDirectory(Path.Combine(folder, "Master")); Directory.CreateDirectory(Path.Combine(folder, "Caves"));
        var world = new WorldInfo(folder, "Rules fixture", "survival", 6, true, true, false, DateTime.Now);
        var choices = new[] { new WorldRuleChoice("默认", new("\"default\"")), new WorldRuleChoice("从不", new("\"never\"")) };
        var catalog = new[] {
            new WorldRuleDefinition("season", "季节", "全局", 0, 0, [], true, choices[0].Value, choices),
            new WorldRuleDefinition("weather", "天气", "环境", 1, 0, [], false, choices[0].Value, choices),
            new WorldRuleDefinition("wormattacks", "洞穴蠕虫", "环境", 1, 1, ["cave"], false, choices[0].Value, choices) };
        var master = Path.Combine(folder, "Master", "leveldataoverride.lua"); var caves = Path.Combine(folder, "Caves", "leveldataoverride.lua");
        const string level = "return {id='SURVIVAL',name='World',location='forest',desc='Keep',overrides={season='default',weather='default',unknown={old=true},world_size='huge'}}";
        File.WriteAllText(master, level); File.WriteAllText(caves, level);
        var snapshot = WorldRules.Read(world);
        check(snapshot.Shards.Count == 2 && snapshot.Shards[0].Values["weather"] == choices[0].Value, "world rules read both shard level settings");
        Dictionary<string, IReadOnlyDictionary<string, LuaValue>> Changes(string shard, string key, LuaValue value) => new() { [shard] = new Dictionary<string, LuaValue> { [key] = value } };
        var backup = WorldRules.Apply(world, snapshot, catalog, Changes("Master", "weather", choices[1].Value), Path.Combine(root, "backups"));
        var current = WorldRules.Read(world);
        check(current.Shards[0].Values["weather"] == choices[1].Value && current.Shards[1].Values["weather"] == choices[0].Value, "master rule save does not change cave rules");
        check(File.ReadAllText(master) == level && current.Shards[0].Values.ContainsKey("unknown") && current.Shards[0].Values["world_size"].Code == "\"huge\"", "rule save preserves complete level definition and unknown map settings");
        check(File.Exists(Path.Combine(backup, "Master", "leveldataoverride.lua")) && File.Exists(Path.Combine(backup, "Master", "worldgenoverride.lua.absent")), "rule backup records originals and absent files");
        bool Reject(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        check(Reject(() => WorldRules.Apply(world, snapshot, catalog, Changes("Master", "weather", choices[0].Value), root)), "stale world rule snapshot cannot overwrite new settings");
        check(Reject(() => WorldRules.Apply(world, current, catalog, Changes("Caves", "season", choices[1].Value), root)), "master-controlled settings cannot be edited on caves");
        check(Reject(() => WorldRules.Apply(world, current, catalog, Changes("Master", "wormattacks", choices[1].Value), root)), "cave-only setting cannot be applied to surface");
        check(Reject(() => WorldRules.Apply(world, current, catalog, Changes("Caves", "weather", new("'injected'")), root)), "world rules reject unlisted values");
        var caveOverride = Path.Combine(folder, "Caves", "worldgenoverride.lua");
        const string existing = "return {override_enabled=true, extra={kept=true},overrides={weather='default',unlisted='keep',world_size='small'}}";
        File.WriteAllText(caveOverride, existing); current = WorldRules.Read(world);
        WorldRules.Apply(world, current, catalog, Changes("Caves", "weather", choices[1].Value), Path.Combine(root, "backups"));
        var output = File.ReadAllText(caveOverride);
        check(output.Contains("[\"extra\"]") && output.Contains("[\"unlisted\"]=\"keep\"") && output.Contains("[\"world_size\"]=\"small\""), "partial overrides preserve unrelated root fields and unknown options");
        File.WriteAllText(caveOverride, "return {override_enabled=false,overrides={weather='never'}}"); current = WorldRules.Read(world);
        check(current.Shards[1].Values["weather"] == choices[0].Value && Reject(() => WorldRules.Apply(world, current, catalog, Changes("Caves", "weather", choices[1].Value), root)), "disabled legacy overrides do not silently activate");
        File.WriteAllText(caveOverride, "return {override_enabled=false,preset='SURVIVAL_TOGETHER',overrides={}}"); current = WorldRules.Read(world);
        check(Reject(() => WorldRules.Apply(world, current, catalog, Changes("Caves", "weather", choices[1].Value), root)), "disabled preset cannot be activated by changing one world rule");
        File.WriteAllText(caveOverride, "return {override_enabled=true,overrides=1}");
        check(Reject(() => WorldRules.Read(world)), "malformed rule overrides fail without changing files");
        var po = "msgctxt \"STRINGS.UI.SANDBOXMENU.TEST\"\nmsgid \"English\"\nmsgstr \"中文\"\n\"续行\"\n\n";
        check(WorldRuleCatalog.ParseTranslations(po)["STRINGS.UI.SANDBOXMENU.TEST"] == "中文续行", "game rule translations support multiline strings");
        check(Reject(() => WorldRuleCatalog.Parse("return {}", new Dictionary<string,string>())), "changed game rule schema fails closed");
        var installed = OperatingSystem.IsWindows() ? Discovery.Installation().Server : "";
        if (File.Exists(installed))
        {
            var actual = WorldRuleCatalog.Load(installed);
            check(actual.All(r => r.IconAtlas == "images/worldsettings_customization.xml" && r.IconName.EndsWith(".tex")) && actual.Single(r => r.Key == "weather").IconName == "rain.tex", "world rules preserve actual game atlas and per-rule images");
            check(actual.Count > 100 && actual.Any(r => r.Key == "autumn" && r.MasterControlled) && actual.Any(r => r.Key == "wormattacks" && r.Available("Caves") && !r.Available("Master")), "installed game metadata yields full world rules and location restrictions");
            check(actual.Any(r => r.Key == "weather" && r.Name.Contains('雨')) && actual.All(r => r.Choices.Count > 0), "installed world rules use game Chinese labels and valid choices");
        }
    }
}
