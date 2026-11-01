using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private readonly SearchFilter ruleSearchFilter;
    private bool refreshingRuleCategories;
    private int ruleColumns;
    private WorldRuleRow[] filteredRules = [];
    private ScrollViewer RulesScroll
    {
        get { RulesGroups.ApplyTemplate(); return (ScrollViewer)RulesGroups.Template.FindName("RulesScrollViewer", RulesGroups); }
    }
    private sealed record RuleBand(string Name, WorldRuleRow[] Rows)
    {
        public Visibility HeadingVisibility => Name.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private IReadOnlyList<WorldRuleDefinition> ruleCatalog = [];
    private List<WorldRuleRow> ruleRows = [];
    private WorldRuleSnapshot? ruleSnapshot;
    private string ruleShard = "Master";
    private int ruleGeneration;
    private bool loadingRules;
    private Task ruleLoadTask = Task.CompletedTask;
    private bool revertingRuleWorldSelection;

    private bool KeepUnsavedRuleWorld(SelectionChangedEventArgs e)
    {
        if (revertingRuleWorldSelection) return true;
        if (App.SmokeDirectory is not null || ruleSnapshot is null || CurrentWorld?.Path == ruleSnapshot.WorldPath || !ruleRows.Any(r => r.Dirty)) return false;
        if (MessageBox.Show(this, "此世界有未保存的规则修改。切换世界会丢弃这些修改，是否继续？", "切换世界", MessageBoxButton.YesNo) == MessageBoxResult.Yes) return false;
        revertingRuleWorldSelection = true;
        try { WorldList.SelectedItem = e.RemovedItems.OfType<WorldInfo>().FirstOrDefault(); }
        finally { revertingRuleWorldSelection = false; }
        return true;
    }

    private async Task SavePendingWorldRulesAsync()
    {
        if (ruleRows.Any(r => r.Dirty)) await SaveWorldRulesAsync();
    }

    private void ResetWorldRules()
    {
        ++ruleGeneration; ruleRows = []; ruleSnapshot = null; ruleShard = "Master"; loadingRules = false;
        if (RulesGroups is null) return;
        filteredRules = []; ruleColumns = 0;
        RulesGroups.ItemsSource = null; RulesCategory.ItemsSource = null; RulesSearch.Clear();
        RulesHeading.Text = "世界规则"; RulesEmpty.Text = "正在读取当前世界规则…"; RulesEmpty.Visibility = Visibility.Visible;
        UpdateRuleEditing();
        if (Tabs.SelectedItem == RulesTab) ruleLoadTask = LoadWorldRulesAsync();
    }
    private async void RulesTab_Selected(object sender, RoutedEventArgs e)
    {
        if (e.Source != RulesTab || !initialized || ruleSnapshot is not null || loadingRules || CurrentWorld is null) return;
        ruleLoadTask = LoadWorldRulesAsync(); await ruleLoadTask;
    }
    private async Task LoadWorldRulesAsync()
    {
        if (CurrentWorld is not { } world || loadingRules) return;
        var generation = ++ruleGeneration; loadingRules = true; UpdateRuleEditing();
        RulesEmpty.Text = "正在读取本机游戏规则…"; RulesEmpty.Visibility = Visibility.Visible;
        var server = ServerPath.Text.Trim();
        try
        {
            var data = await Task.Run(() =>
            {
                var catalog = WorldRuleCatalog.Load(server);
                return (Catalog: catalog, Snapshot: WorldRules.Read(world), Icons: WorldRuleImages.Load(server, catalog, QueueSessionLog));
            });
            if (generation != ruleGeneration || CurrentWorld?.Path != world.Path) return;
            ruleCatalog = data.Catalog; ruleSnapshot = data.Snapshot;
            ruleRows = data.Snapshot.Shards.SelectMany(shard => ruleCatalog.Where(d => d.Available(shard.Shard) && (shard.Shard == "Master" || !d.MasterControlled))
                .Select(d => new WorldRuleRow(d, shard.Shard, shard.Values.GetValueOrDefault(d.Key), data.Icons.GetValueOrDefault(d.Key)))).ToList();
            foreach (var row in ruleRows) row.Modified += UpdateRuleEditing;
            RulesHeading.Text = "世界规则 · " + world.Name;
            RulesHint.Text = "修改规则后保存，下次开服生效。主世界统一控制季节等全局规则；此页不会重新生成地图。";
            RefreshRuleCategories();
        }
        catch (Exception ex)
        {
            if (generation != ruleGeneration) return;
            ruleSnapshot = null; ruleRows = []; RulesGroups.ItemsSource = null;
            RulesEmpty.Text = "无法读取世界规则：" + ex.Message; AppendLog("世界规则读取失败：" + ex.Message);
        }
        finally { if (generation == ruleGeneration) { loadingRules = false; UpdateRuleEditing(); } }
    }
    private void MasterRules_Click(object sender, RoutedEventArgs e) { ruleShard = "Master"; RefreshRuleCategories(); }
    private void CavesRules_Click(object sender, RoutedEventArgs e) { ruleShard = "Caves"; RefreshRuleCategories(); }
    private void RefreshRuleCategories()
    {
        if (RulesCategory is null) return;
        var groups = ruleRows.Where(r => r.Shard == ruleShard).Select(r => r.Definition.Group).Distinct().Select(g => new { Key = g, Label = g }).ToList();
        groups.Insert(0, new { Key = "", Label = "全部规则分类" });
        refreshingRuleCategories = true;
        try { RulesCategory.ItemsSource = groups; RulesCategory.SelectedIndex = 0; }
        finally { refreshingRuleCategories = false; }
        ApplyRuleFilter(); UpdateRuleEditing(); RulesScroll.ScrollToTop();
    }
    private void RulesCategory_Changed(object sender, SelectionChangedEventArgs e) { if (!refreshingRuleCategories) ApplyRuleFilter(); }
    private void RulesGroups_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ApplyRuleFilter();
    }
    private void ApplyRuleFilter()
    {
        if (RulesGroups is null || RulesSearch is null) return;
        var search = RulesSearch.Text.Trim(); var group = RulesCategory.SelectedValue as string ?? "";
        var rows = ruleRows.Where(r => r.Shard == ruleShard && (group.Length == 0 || r.Definition.Group == group) &&
            (search.Length == 0 || (r.Name + r.Definition.Key).Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        // Virtualize complete rows of cards, preserving the grouped, responsive card layout.
        // Only viewport rows create ComboBoxes; off-screen values stay in ruleRows.
        var columns = Math.Max(1, (int)((RulesGroups.ActualWidth - 20) / 352));
        if (ruleColumns != columns || !filteredRules.SequenceEqual(rows) || RulesGroups.ItemsSource is null)
        {
            ruleColumns = columns; filteredRules = rows;
            RulesGroups.ItemsSource = rows.GroupBy(r => r.Definition.Group).SelectMany(g => g.Chunk(columns)
                .Select((band, index) => new RuleBand(index == 0 ? g.Key : "", band))).ToArray();
            RulesScroll.ScrollToTop();
        }
        RulesEmpty.Visibility = rows.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (ruleSnapshot is not null) RulesEmpty.Text = "没有符合筛选条件的规则。";
    }
    private void UpdateRuleEditing()
    {
        if (SaveRulesButton is null) return;
        var editable = !busy && !session.HasProcesses && !loadingRules && ruleSnapshot is not null;
        foreach (var row in ruleRows) row.Editable = editable;
        var count = ruleRows.Count(r => r.Dirty);
        SaveRulesButton.IsEnabled = editable && count > 0;
        ReloadRulesButton.IsEnabled = !busy && !loadingRules;
        RulesRunHint.Visibility = session.HasProcesses ? Visibility.Visible : Visibility.Collapsed;
        CavesRulesButton.IsEnabled = CurrentWorld?.HasCaves == true;
        MasterRulesButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ruleShard == "Master" ? "#F5A962" : "#363C4F"));
        MasterRulesButton.Foreground = ruleShard == "Master" ? new SolidColorBrush(Color.FromRgb(30, 35, 48)) : Brushes.White;
        CavesRulesButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ruleShard == "Caves" ? "#F5A962" : "#363C4F"));
        CavesRulesButton.Foreground = ruleShard == "Caves" ? new SolidColorBrush(Color.FromRgb(30, 35, 48)) : Brushes.White;
        RulesSaveHint.Text = count > 0 ? $"{count} 项未保存 · 主世界与洞穴分别应用" : "主世界和洞穴分别保存 · 保存前自动备份";
    }
    private async void ReloadRules_Click(object sender, RoutedEventArgs e)
    {
        if (ruleRows.Any(r => r.Dirty) && App.SmokeDirectory is null && MessageBox.Show(this, "重新读取会丢弃未保存的世界规则修改，是否继续？", "重新读取规则", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await LoadWorldRulesAsync();
    }
    private async void SaveRules_Click(object sender, RoutedEventArgs e) => await Guard(SaveWorldRulesAsync);
    private async Task SaveWorldRulesAsync()
    {
        RequireIdle(); var world = RequireWorld();
        var snapshot = ruleSnapshot ?? throw new InvalidOperationException("请先读取世界规则。");
        var changes = ruleRows.Where(r => r.Dirty).GroupBy(r => r.Shard).ToDictionary(g => g.Key,
            g => (IReadOnlyDictionary<string, LuaValue>)g.ToDictionary(r => r.Definition.Key, r => r.Selected.Value));
        var backup = await Task.Run(() => WorldRules.Apply(world, snapshot, ruleCatalog, changes, store.BackupRoot));
        AppendLog("世界规则已保存，备份：" + backup);
        await LoadWorldRulesAsync(); StatusText.Text = "世界规则已保存，下次开服生效。";
    }
}
