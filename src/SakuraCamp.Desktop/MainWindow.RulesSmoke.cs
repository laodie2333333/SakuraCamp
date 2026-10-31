using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private async Task VerifyWorldRulesSmokeAsync(string directory)
    {
        var worlds = WorldList.Items.Cast<WorldInfo>().ToArray(); var selected = CurrentWorld; var oldTab = Tabs.SelectedItem;
        var originalWidth = Width; var originalHeight = Height;
        var fixture = Path.Combine(directory, "world-rule-fixture");
        foreach (var shard in new[] { "Master", "Caves" })
        {
            Directory.CreateDirectory(Path.Combine(fixture, shard));
            File.WriteAllText(Path.Combine(fixture, shard, "server.ini"), "[NETWORK]\nserver_port=" + (shard == "Master" ? "31280" : "31281"));
            File.WriteAllText(Path.Combine(fixture, shard, "leveldataoverride.lua"), "return {id='SURVIVAL',name='Fixture',desc='',location='" + (shard == "Master" ? "forest" : "cave") + "',overrides={weather='default',unknown_setting={keep=true}}}");
        }
        File.WriteAllText(Path.Combine(fixture, "cluster.ini"), "[NETWORK]\ncluster_name=世界规则验证\n");
        File.WriteAllText(Path.Combine(fixture, "Master", "worldgenoverride.lua"), "KLEI     1 return {override_enabled=true,overrides={weather='default',world_size='small'}}");
        var world = Discovery.ReadWorld(fixture);
        var levelOriginal = File.ReadAllBytes(Path.Combine(fixture, "Master", "leveldataoverride.lua"));
        try
        {
            Tabs.SelectedItem = OverviewTab;
            WorldList.ItemsSource = new[] { world }; WorldList.SelectedItem = world; await modRefreshTask;
            Tabs.SelectedItem = RulesTab; await ruleLoadTask; UpdateLayout();
            if (ruleSnapshot is null || ruleRows.Count < 100) throw new InvalidOperationException("World rule page did not load game definitions: " + RulesEmpty.Text);
            var icons = ruleRows.Where(r => r.Icon is not null).ToArray();
            if (icons.Length != ruleRows.Count || ruleRows.Single(r => r.Shard == "Master" && r.Definition.Key == "weather").Icon is null)
                throw new InvalidOperationException($"Game rule icons missing: {icons.Length}/{ruleRows.Count}");
            if (!VisualChildren<Image>(RulesGroups).Any(i => i.Source is not null)) throw new InvalidOperationException("Rule icons not bound to cards");
            await VerifyRulePerformanceSmokeAsync(directory);
            var order = Tabs.Items.Cast<TabItem>().Select(t => t.Header as string).ToArray();
            if (string.Join(",", order) != "世界总览,联机设置,世界规则,Mod 管理,服务器状态,文件目录,日志与诊断") throw new InvalidOperationException("Navigation order does not match requested order");
            var master = ruleRows.Single(r => r.Shard == "Master" && r.Definition.Key == "weather");
            var cave = ruleRows.Single(r => r.Shard == "Caves" && r.Definition.Key == "weather");
            RulesSearch.Text = master.Name; ruleSearchFilter.Flush(); UpdateLayout();
            var editors = VisualChildren<ComboBox>(RulesGroups).Where(c => c.DataContext is WorldRuleRow).ToArray();
            var masterEditor = editors.Single(c => c.DataContext == master);
            masterEditor.SelectedItem = master.Choices.Single(c => c.Value.Code == "\"never\"");
            await Task.Delay(40);
            if (!master.Dirty || cave.Dirty || !SaveRulesButton.IsEnabled) throw new InvalidOperationException("Master rule editing affected caves or did not mark pending changes");
            CavesRulesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
            var caveEditor = VisualChildren<ComboBox>(RulesGroups).Single(c => c.DataContext == cave);
            caveEditor.SelectedItem = cave.Choices.Single(c => c.Value.Code == "\"often\"");
            RulesSearch.Clear(); ruleSearchFilter.Flush(); UpdateLayout();
            if (ruleRows.Any(r => r.Shard == "Caves" && r.Definition.MasterControlled)) throw new InvalidOperationException("Caves exposed ignored master-controlled options");
            SaveSmokeScreenshot(Path.Combine(directory, "world-rules-caves.png"));
            MasterRulesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); UpdateLayout();
            SaveSmokeScreenshot(Path.Combine(directory, "world-rules-master.png"));
            busy = true; UpdateRuleEditing();
            if (SaveRulesButton.IsEnabled || ruleRows.Any(r => r.Editable)) throw new InvalidOperationException("Busy state did not lock world rules");
            busy = false; UpdateRuleEditing();
            SaveRulesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (busy && DateTime.UtcNow < deadline) await Task.Delay(50);
            if (busy || ruleRows.Any(r => r.Dirty)) throw new InvalidOperationException("World rule save did not complete");
            var saved = WorldRules.Read(world);
            if (saved.Shards[0].Values["weather"].Code != "\"never\"" || saved.Shards[1].Values["weather"].Code != "\"often\"" || !File.ReadAllBytes(Path.Combine(fixture, "Master", "leveldataoverride.lua")).SequenceEqual(levelOriginal))
                throw new InvalidOperationException("World rule UI save failed or overwrote level definitions");
            var hunger = ruleRows.Single(r => r.Shard == "Master" && r.Definition.Key == "hunger");
            hunger.Selected = hunger.Choices.Single(c => c.Value.Code == "\"nonlethal\"");
            Tabs.SelectedItem = ConnectionTab;
            await Guard(SavePendingWorldRulesAsync);
            if (Tabs.SelectedItem != ConnectionTab || WorldRules.Read(world).Shards[0].Values["hunger"].Code != "\"nonlethal\"")
                throw new InvalidOperationException("Pending rules were not saved before launch or saving navigated away");
            Tabs.SelectedItem = RulesTab;
            Width = MinWidth; Height = MinHeight; UpdateLayout();
            SaveSmokeScreenshot(Path.Combine(directory, "minimum-rules.png"));
            if (SystemParameters.HighContrast || !backdropEnabled)
            { if (WindowSurface.Background is not SolidColorBrush { Color.A: 255 }) throw new InvalidOperationException("Backdrop fallback is not opaque"); }
            else if (WindowSurface.Background is not SolidColorBrush { Color.A: < 255 } || Opacity != 1 || AllowsTransparency)
                throw new InvalidOperationException("Window background is not translucent or text/window was faded");
            await VerifyBackdropSmokeAsync(directory);
            File.WriteAllText(Path.Combine(directory, "world-rules-report.json"), System.Text.Json.JsonSerializer.Serialize(new
            { Definitions = ruleCatalog.Count, MainRules = ruleRows.Count(r => r.Shard == "Master"), CaveRules = ruleRows.Count(r => r.Shard == "Caves"), NavigationOrder = order,
                KleiHeaderReadAndSaved = File.ReadAllText(Path.Combine(fixture, "Master", "worldgenoverride.lua")).StartsWith("KLEI     1 return "), SeparateShardValues = true, SaveButtonVerified = true, PendingRulesSaveWithoutNavigation = true, LevelDefinitionPreserved = true, MasterControlRespected = true, BlurredBackdrop = backdropEnabled, OpaqueText = true, RuleIcons = icons.Length, AllRuleIconsLoaded = true }));
        }
        finally
        {
            busy = false; Tabs.SelectedItem = OverviewTab; WorldList.ItemsSource = worlds; WorldList.SelectedItem = selected; await modRefreshTask;
            Width = originalWidth; Height = originalHeight; Tabs.SelectedItem = oldTab; UpdateControls();
        }
    }
}
