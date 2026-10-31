using System.IO;
using System.Net.Http;
using System.Windows;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private bool restoringNodeDetails;
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
        var id = nodeId ?? profile?.CreationNodeId ?? profile?.SavedNode?.Id;
        var available = creationNodes.Where(n => n.IsAvailable(CreationScope.SelectedIndex == 1)).OrderBy(n => n.Vip).ThenBy(n => n.Id).ToList();
        if (id is { } savedId && !available.Any(n => n.Id == savedId))
        {
            var known = creationNodes.FirstOrDefault(n => n.Id == savedId)
                ?? (profile?.SavedNode?.Id == savedId ? profile.SavedNode : null);
            if (known is null)
            {
                var address = profile?.MasterAddress ?? "";
                var domain = host.Length > 0 ? host : address.Split(':')[0];
                known = new(savedId, $"已保存节点 {savedId}", domain, 0, 0);
            }
            available.Add(known);
        }
        CreationNode.ItemsSource = available;
        CreationNode.SelectedValue = id;
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
            throw new InvalidOperationException("当前世界已有其他节点的隧道。请选回原节点；更换节点前需先在樱花面板处理原隧道，避免重复创建。");
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
