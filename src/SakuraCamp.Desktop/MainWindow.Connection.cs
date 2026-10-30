using System.IO;
using System.Windows;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
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
        // Tunnel creation keeps the existing mutation journal and recovery safeguards.
        await EnsureConnectionTunnelsAsync();
        var ini = IniDocument.Load(Path.Combine(world.Path, "cluster.ini"));
        ini.Set("NETWORK", "cluster_password", password);
        var backup = await Task.Run(() => WorldFiles.Backup(world.Path, store.BackupRoot));
        ini.Save(Path.Combine(world.Path, "cluster.ini"));
        if (token.Length > 0) AtomicFile.Write(Path.Combine(world.Path, "cluster_token.txt"), token);
        KleiToken.Clear(); TokenHint.Text = "已保存科雷令牌；留空可继续使用。";
        AppendLog("联机设置已保存，世界备份：" + backup);
        StatusText.Text = "联机设置已保存，隧道与地址已自动配置。返回世界总览即可一键开服。";
    });
}
