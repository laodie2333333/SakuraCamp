using System.IO;
using System.Windows;
using System.Windows.Controls;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private IReadOnlyList<InstalledMod> installedMods = [];
    private IReadOnlyList<ModCard> modCards = [];
    private WorldModSnapshot? modSnapshot;
    private int modScanGeneration;
    private Task modRefreshTask = Task.CompletedTask;

    private async Task RefreshModManagerAsync(WorldInfo world, bool automatic = false)
    {
        var generation = ++modScanGeneration;
        var workshop = WorkshopPath.Text.Trim(); var server = ServerPath.Text.Trim();
        var offset = ModScroll.VerticalOffset;
        if (!automatic)
        {
            ModHeading.Text = "正在读取本地服务器 Mod…";
            modSnapshot = null;
            foreach (var card in modCards) card.SetEditable(false);
        }
        var result = await Task.Run(() =>
        {
            var fingerprint = ModFingerprint(workshop, server, world);
            WorldModSnapshot? snapshot = null; var error = "";
            try { snapshot = WorldMods.Read(world); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { error = ex.Message; }
            var catalog = ModCatalog.Scan(workshop, server, snapshot?.Keys);
            // Client-only mods never appear in the server management UI.
            var serverMods = catalog.Where(m => !m.ClientOnly).ToArray();
            var cards = serverMods.Select(m => new ModCard(m, snapshot, ModImages.Load(m))).ToArray();
            return (snapshot, serverMods, cards, error, fingerprint);
        });
        if (generation != modScanGeneration || CurrentWorld?.Path != world.Path || (automatic && (busy || activeModSettings is not null || modWatchClosed))) return;
        modSnapshot = result.snapshot; installedMods = result.serverMods; modCards = result.cards;
        modFingerprint = result.fingerprint;
        modRetryAt = result.error.Length > 0 || installedMods.Any(m => m.Downloaded && m.Error.Length > 0)
            ? DateTimeOffset.UtcNow.AddSeconds(15) : DateTimeOffset.MaxValue;
        mods = installedMods.Where(m => modSnapshot?.AnyEnabled(m.Key) == true).Select(m => new ModInfo(m.WorkshopId.Length > 0 ? m.WorkshopId : m.Key, m.Name, m.Version, m.Downloaded)).ToArray();
        var enabled = installedMods.Count(m => modSnapshot?.AnyEnabled(m.Key) == true);
        ModHeading.Text = $"服务器 Mod · 本地 {installedMods.Count(m => m.Downloaded)} 个 · 已启用 {enabled} 个";
        ModManagerHint.Text = result.error.Length > 0 ? "世界 Mod 配置无法读取，已暂停编辑：" + result.error : $"当前世界：{world.Name} · 自动识别本地新增与删除。启用与配置保存到地上和洞穴，开服后生效。";
        ApplyModFilter(); UpdateModEditing();
        if (automatic) ModScroll.ScrollToVerticalOffset(offset);
    }

    private void ApplyModFilter()
    {
        if (ModList is null || ModSearch is null || ModFilter is null) return;
        var search = ModSearch.Text.Trim();
        var index = ModFilter.SelectedIndex;
        var visible = modCards.Where(c => search.Length == 0 || (c.Name + " " + c.Mod.Author + " " + c.Mod.Key).Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Where(c => index switch { 1 => c.Enabled, 2 => !c.Enabled, 3 => !c.Mod.Downloaded, _ => true })
            .OrderByDescending(c => c.Enabled).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        ModList.ItemsSource = visible;
        ModEmpty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModEmpty.Text = modCards.Count == 0 ? "未找到服务器 Mod。请先在 Steam 下载，或在文件目录中核对创意工坊路径。" : "没有符合筛选条件的服务器 Mod。";
    }

    private void UpdateModEditing()
    {
        foreach (var card in modCards) card.SetEditable(modSnapshot is not null && !busy && !session.HasProcesses);
        if (ModRunHint is not null) ModRunHint.Visibility = session.HasProcesses ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ModSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyModFilter();
    private void ModFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyModFilter();

    private async void ToggleMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModCard card) return;
        await Guard(async () =>
        {
            RequireIdle(); var world = RequireWorld();
            var snapshot = modSnapshot ?? throw new InvalidOperationException("本地 Mod 配置尚未就绪，请等待自动更新后重试。");
            var enabled = card.EnableOnToggle;
            var backup = await Task.Run(() => WorldMods.Apply(world, snapshot, card.Mod, enabled, null, snapshot.Files.Select(f => f.Shard).ToArray(), store.BackupRoot));
            AppendLog($"Mod {card.Name} 已{(enabled ? "启用" : "停用")}；配置备份：{backup}");
            await RefreshModManagerAsync(world);
            StatusText.Text = $"已{(enabled ? "启用" : "停用")} {card.Name}，下次开服生效。";
        });
    }

    private async void ConfigureMod_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ModCard card) return;
        await Guard(async () =>
        {
            RequireIdle(); var world = RequireWorld();
            var snapshot = modSnapshot ?? throw new InvalidOperationException("本地 Mod 正在更新，请稍后重试。");
            WorldMods.CheckCurrent(snapshot);
            var dialog = new ModSettingsPanel(card.Mod, snapshot, async (changes, shards) =>
            {
                if (changes.Count == 0) return;
                RequireIdle();
                var backup = await Task.Run(() => WorldMods.Apply(world, snapshot, card.Mod, null, changes, shards, store.BackupRoot));
                AppendLog($"Mod {card.Name} 的 {changes.Count} 个选项已保存；配置备份：{backup}");
                await RefreshModManagerAsync(world);
                StatusText.Text = $"已保存 {card.Name} 的配置，下次开服生效。";
            });
            await ShowModSettingsAsync(dialog);
        });
    }
    private void OpenModWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModCard card && card.Mod.Url.Length > 0) Open(card.Mod.Url);
    }
    private void ModCard_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is ModCard card && card.Mod.Url.Length > 0) Open(card.Mod.Url);
    }
}
