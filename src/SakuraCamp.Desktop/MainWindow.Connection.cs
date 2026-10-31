using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private bool restoringNodeDetails;
    private int suppressNodeSelection;
    private Func<NodeInfo, Task>? nodeSelectionSmokeAction;
    private Task nodeSelectionTask = Task.CompletedTask;
    private async void CreationNode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || suppressNodeSelection > 0 || busy || (App.SmokeDirectory is not null && nodeSelectionSmokeAction is null) || CreationNode.SelectedItem is not NodeInfo node) return;
        nodeSelectionTask = Guard(() => nodeSelectionSmokeAction is { } smoke ? smoke(node) : ChangeSelectedNodeAsync(node));
        await nodeSelectionTask;
    }

    private async void RetryNodeChange_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        settings.Profiles.TryGetValue(RequireWorld().Path, out var profile);
        var node = profile?.NodeChange?.Target ?? CreationNode.SelectedItem as NodeInfo
            ?? throw new InvalidOperationException("请先选择节点。");
        await ChangeSelectedNodeAsync(node);
    });

    private async Task ChangeSelectedNodeAsync(NodeInfo node)
    {
        var world = RequireWorld();
        var token = SakuraToken.Password.Trim();
        if (!settings.Profiles.TryGetValue(world.Path, out var profile)) settings.Profiles[world.Path] = profile = new();
        try
        {
            RequireIdle();
            if (token.Length == 0) throw new InvalidOperationException("请先填写樱花访问密钥，再选择节点。");
            var oldIds = new[] { profile.MasterTunnelId, profile.CavesTunnelId }.Where(id => id.HasValue).ToHashSet();
            if (settings.Profiles.Any(p => !p.Key.Equals(world.Path, StringComparison.OrdinalIgnoreCase) &&
                (oldIds.Contains(p.Value.MasterTunnelId) || oldIds.Contains(p.Value.CavesTunnelId) ||
                profile.CreationKey.Length > 0 && p.Value.CreationKey == profile.CreationKey)))
                throw new InvalidOperationException("当前通道同时被其他世界绑定，不能自动删除。请先核对世界的通道方案。");
            if (profile.CreationNodeId is null) profile.PublicServer = CreationScope.SelectedIndex == 1;
            StatusText.Text = $"正在为当前世界配置节点：{node.Name}…";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var pair = await new SakuraClient(http).ChangeWorldNodeAsync(token, profile, node.Id, world.HasCaves, Persist,
                line => { var safe = LogReader.Redact(line, token); AppendLog("[樱花] " + safe); StatusText.Text = safe; });
            var tunnels = pair.Caves is { } caves ? new[] { pair.Master, caves } : new[] { pair.Master };
            MasterTunnel.ItemsSource = tunnels; CavesTunnel.ItemsSource = tunnels;
            MasterTunnel.SelectedValue = pair.Master.Id; CavesTunnel.SelectedValue = pair.Caves?.Id;
            UpdateTunnelFields(); SaveProfile();
            SecretStore.Save(SecretPath, token);
            StatusText.Text = world.HasCaves ? "已绑定所选节点的地上和洞穴新通道，可直接一键开服。" : "已绑定所选节点的地上通道，可直接一键开服。";
        }
        catch
        {
            // A failed replacement must never leave the old, deleted addresses available to copy or launch.
            if (profile.NodeChange is not null) { MasterTunnel.ItemsSource = null; CavesTunnel.ItemsSource = null; UpdateTunnelFields(); }
            throw;
        }
        finally
        {
            RestoreWorldNode(profile, profile.NodeChange?.Target.Id ?? profile.CreationNodeId);
            RetryNodeChange.Visibility = profile.NodeChange is not null || profile.PendingTunnelShards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RecoveryButton.Visibility = profile.PendingTunnelShards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private async void ConnectionTab_Selected(object sender, RoutedEventArgs e)
    {
        if (e.Source != Tabs || Tabs.SelectedItem != ConnectionTab || !initialized || restoringNodeDetails || App.SmokeDirectory is not null || SakuraToken.Password.Trim().Length == 0 || CurrentWorld is not { } world) return;
        var id = (CreationNode.SelectedItem as NodeInfo)?.Id;
        settings.Profiles.TryGetValue(world.Path, out var savedProfile);
        var hasLegacyTunnels = savedProfile is not null && (savedProfile.MasterTunnelId.HasValue || savedProfile.MasterAddress.Length > 0 || savedProfile.CreationKey.Length > 0);
        if (id is null && !hasLegacyTunnels || id.HasValue && creationNodes.Any(n => n.Id == id)) return;
        var key = SakuraToken.Password.Trim();
        restoringNodeDetails = true;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var api = new SakuraClient(http);
            var restoredId = id;
            if (restoredId is null && savedProfile is not null)
            {
                var tunnels = await api.TunnelsAsync(key);
                restoredId = SakuraClient.SelectWorldTunnels(tunnels, savedProfile, world.HasCaves).Master.Node;
            }
            var nodes = await api.NodesAsync(key);
            if (CurrentWorld?.Path != world.Path || SakuraToken.Password.Trim() != key || busy || (CreationNode.SelectedItem as NodeInfo)?.Id != id) return;
            creationNodes = nodes;
            settings.Profiles.TryGetValue(world.Path, out var profile);
            if (profile is not null)
            {
                profile.CreationNodeId ??= restoredId;
                if (nodes.FirstOrDefault(n => n.Id == restoredId) is { } known) profile.SavedNode = known;
                Persist();
            }
            RestoreWorldNode(profile, restoredId);
        }
        catch (Exception ex) { AppendLog("读取已保存节点名称失败，保留当前节点显示：" + LogReader.Redact(ex.Message, key)); }
        finally { restoringNodeDetails = false; }
    }

    private void LoadWorldCredentials(WorldInfo world)
    {
        WorldPassword.Password = WorldFiles.Password(world);
        var path = Path.Combine(world.Path, "cluster_token.txt");
        KleiToken.Password = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        TokenHint.Text = KleiToken.Password.Length == 0 ? "当前世界尚未保存科雷令牌。" : "已读取当前世界的科雷令牌，框内圆点代表已保存的内容。";
    }

    private void RestoreWorldNode(WorldProfile? profile, int? nodeId = null, string host = "")
    {
        var id = nodeId ?? profile?.NodeChange?.Target.Id ?? profile?.CreationNodeId ?? profile?.SavedNode?.Id;
        var available = creationNodes.Where(n => n.IsAvailable(CreationScope.SelectedIndex == 1)).OrderBy(n => n.Vip).ThenBy(n => n.Id).ToList();
        if (id is { } savedId && !available.Any(n => n.Id == savedId))
        {
            var known = (profile?.NodeChange?.Target.Id == savedId ? profile.NodeChange.Target : null)
                ?? creationNodes.FirstOrDefault(n => n.Id == savedId)
                ?? (profile?.SavedNode?.Id == savedId ? profile.SavedNode : null);
            if (known is null)
            {
                var address = profile?.MasterAddress ?? "";
                var domain = host.Length > 0 ? host : address.Split(':')[0];
                known = new(savedId, $"已保存节点 {savedId}", domain, 0, 0);
            }
            available.Add(known);
        }
        suppressNodeSelection++;
        try { CreationNode.ItemsSource = available; CreationNode.SelectedValue = id; }
        finally { suppressNodeSelection--; }
    }

    private void RememberWorldNode(WorldProfile profile, TunnelInfo tunnel)
    {
        var node = creationNodes.FirstOrDefault(n => n.Id == tunnel.Node)
            ?? (CreationNode.SelectedItem is NodeInfo selected && selected.Id == tunnel.Node ? selected : null)
            ?? (profile.SavedNode?.Id == tunnel.Node ? profile.SavedNode : null)
            ?? new NodeInfo(tunnel.Node, $"已保存节点 {tunnel.Node}", tunnel.NodeHost, 0, 0);
        profile.SavedNode = node;
        profile.CreationNodeId ??= tunnel.Node;
        RestoreWorldNode(profile, tunnel.Node, tunnel.NodeHost);
    }

    private async Task EnsureConnectionTunnelsAsync()
    {
        var world = RequireWorld();
        settings.Profiles.TryGetValue(world.Path, out var profile);
        if (profile?.NodeChange is { } change) await ChangeSelectedNodeAsync(change.Target);
        // Use the persisted ownership first; never pick arbitrary tunnels from the account.
        await LoadTunnelsAsync();
        if (MasterTunnel.SelectedItem is not TunnelInfo || (world.HasCaves && CavesTunnel.SelectedItem is not TunnelInfo))
        {
            if (CreationNode.SelectedItem is null && profile?.CreationNodeId is not null) await LoadNodesAsync();
            try { await CreateTunnelsAsync(); }
            finally { RecoveryButton.Visibility = settings.Profiles.GetValueOrDefault(world.Path)?.PendingTunnelShards.Count > 0 ? Visibility.Visible : Visibility.Collapsed; }
        }
        var pair = SakuraClient.SelectWorldTunnels(MasterTunnel.Items.Cast<TunnelInfo>().ToArray(),
            settings.Profiles.GetValueOrDefault(world.Path) ?? new(), world.HasCaves,
            (MasterTunnel.SelectedItem as TunnelInfo)?.Id, (CavesTunnel.SelectedItem as TunnelInfo)?.Id);
        if (CreationNode.SelectedItem is NodeInfo node && pair.Master.Node != node.Id)
            throw new InvalidOperationException("当前通道与所选节点不一致。请在节点下拉框重新选择目标节点，完成自动换节点后再开服。");
        MasterTunnel.SelectedValue = pair.Master.Id; CavesTunnel.SelectedValue = pair.Caves?.Id;
        UpdateTunnelFields(); SaveProfile();
    }

    private async void SaveConnection_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        RequireIdle(); var world = RequireWorld();
        var token = KleiToken.Password.Trim(); var password = WorldPassword.Password;
        if (string.IsNullOrWhiteSpace(SakuraToken.Password)) throw new InvalidOperationException("请填写樱花访问密钥。");
        if (token.Length > 0 && !LaunchPlan.IsToken(token)) throw new InvalidOperationException("请粘贴完整的 pds- 开头的科雷独立服务器令牌。");
        if (token.Length == 0 && !File.Exists(Path.Combine(world.Path, "cluster_token.txt"))) throw new InvalidOperationException("当前世界没有科雷令牌，请先填写令牌。");
        var ini = IniDocument.Load(Path.Combine(world.Path, "cluster.ini"));
        ini.Set("NETWORK", "cluster_password", password);
        // Tunnel creation keeps the existing mutation journal and recovery safeguards.
        await EnsureConnectionTunnelsAsync();
        var backup = await Task.Run(() => WorldFiles.Backup(world.Path, store.BackupRoot));
        ini.Save(Path.Combine(world.Path, "cluster.ini"));
        if (token.Length > 0) AtomicFile.Write(Path.Combine(world.Path, "cluster_token.txt"), token);
        LoadWorldCredentials(world);
        AppendLog("联机设置已保存，世界备份：" + backup);
        StatusText.Text = "联机设置已保存，隧道与地址已自动配置。可以在本页点击一键开服。";
    });
}
