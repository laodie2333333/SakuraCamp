using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow : Window
{
    private readonly SettingsStore store = new(App.DataRoot);
    private readonly ManagedSession session = new();
    private readonly Queue<string> logLines = new();
    private AppSettings settings = new();
    private IReadOnlyList<ModInfo> mods = [];
    private IReadOnlyList<NodeInfo> creationNodes = [];
    private bool initialized;
    private bool busy;
    private WorldInfo? CurrentWorld => WorldList.SelectedItem as WorldInfo;
    private string SecretPath => Path.Combine(store.Root, "sakura-key.dpapi");
    public MainWindow()
    {
        InitializeComponent();
        ruleSearchFilter = new SearchFilter(RulesSearch, ApplyRuleFilter);
        modSearchFilter = new SearchFilter(ModSearch, ApplyModFilter);
        InitializeLogging();
        session.Log += QueueSessionLog;
        session.State += (name, state) => Dispatcher.BeginInvoke(() =>
        {
            StatusFor(name).Text = state;
            if (name == "樱花" && state == "已停止") ShowTunnelHealth("本次通道管理已结束", logChange: true);
            UpdateControls();
        });
        Loaded += async (_, _) => {
            await Guard(InitializeAsync);
            InitializeLiveStatus();
            await LoadAvatarsAsync();
            InitializeTunnelStatus();
            InitializeModWatching();
            if (App.SmokeDirectory is { } directory) await SmokeAsync(directory);
        };

        InitializeWindowChrome();
    }

    private T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
        while (parent != null)
        {
            if (parent is T typedParent) return typedParent;
            parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
        }
        return null;
    }
    private TextBlock StatusFor(string name) => name switch { "Master" => MasterStatus, "Caves" => CavesStatus, _ => TunnelStatus };
    private async Task InitializeAsync()
    {
        settings = store.Load(); var found = await Task.Run(Discovery.Installation);
        RootPath.Text = settings.SaveRoot.Length > 0 ? settings.SaveRoot : Discovery.DefaultSaveRoot;
        ServerPath.Text = settings.ServerExe.Length > 0 ? settings.ServerExe : found.Server;
        WorkshopPath.Text = settings.WorkshopPath.Length > 0 ? settings.WorkshopPath : found.Workshop;
        FrpcPath.Text = settings.FrpcExe.Length > 0 ? settings.FrpcExe : found.Frpc;
        if (FrpcPath.Text.Length > 0 && FrpcInstaller.IsLauncherCompanion(FrpcPath.Text))
        {
            FrpcPath.Text = found.Frpc;
            AppendLog("旧设置选中了樱花启动器专用 frpc，已移除。自动开服时将获取官方独立版。");
        }
        try { SakuraToken.Password = SecretStore.Load(SecretPath); RememberToken.IsChecked = true; } catch (Exception ex) { AppendLog("读取已保存访问密钥失败：" + ex.Message); }
        initialized = true; await ScanAsync();
    }
    private async Task ScanAsync(string? select = null)
    {
        var root = RootPath.Text.Trim();
        var worlds = await Task.Run(() => Discovery.Worlds(root));
        WorldList.ItemsSource = worlds; WorldCount.Text = worlds.Count > 0 ? $"发现 {worlds.Count} 个本地世界" : "未找到存档 · 可导入已有世界";
        WorldList.SelectedItem = worlds.FirstOrDefault(w => w.Path.Equals(select ?? settings.LastWorld, StringComparison.OrdinalIgnoreCase)) ?? worlds.FirstOrDefault();
        await modRefreshTask;
        Persist(); StatusText.Text = worlds.Count > 0 ? "已读取本地世界。在联机设置中保存密钥与节点后即可开服。" : "请在游戏中创建世界并退出，或点击导入存档。"; UpdateControls();
    }
    private void Persist() { settings.SaveRoot = RootPath.Text.Trim(); settings.ServerExe = ServerPath.Text.Trim(); settings.WorkshopPath = WorkshopPath.Text.Trim(); settings.FrpcExe = FrpcPath.Text.Trim(); if (CurrentWorld is not null) settings.LastWorld = CurrentWorld.Path; store.Save(settings); }
    private void SaveProfile()
    {
        var world = RequireWorld();
        settings.Profiles.TryGetValue(world.Path, out var previous);
        previous ??= new();
        previous.AutomaticTunnels = true;
        previous.MasterAddress = MasterAddress.Text.Trim(); previous.CavesAddress = CavesAddress.Text.Trim();
        previous.MasterTunnelId = (MasterTunnel.SelectedItem as TunnelInfo)?.Id ?? previous.MasterTunnelId;
        previous.CavesTunnelId = (CavesTunnel.SelectedItem as TunnelInfo)?.Id ?? previous.CavesTunnelId;
        if (MasterTunnel.SelectedItem is TunnelInfo master) RememberWorldNode(previous, master);
        settings.Profiles[world.Path] = previous;
        Persist();
    }
    private WorldInfo RequireWorld() => CurrentWorld ?? throw new InvalidOperationException("请先返回“世界总览”选择一个世界。");
    private void RequireIdle() { if (session.HasProcesses) throw new InvalidOperationException("请先保存关服，再更改世界或设置。"); WorldFiles.RequireStopped(); }
    private async void WorldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        if (KeepUnsavedRuleWorld(e)) return;
        if (CurrentWorld is null)
        {
            ++modScanGeneration; modSnapshot = null; installedMods = []; modCards = []; mods = [];
            KleiToken.Clear(); WorldPassword.Clear(); CreationNode.SelectedIndex = -1;
            ModHeading.Text = "服务器 Mod"; ModManagerHint.Text = "请先在世界总览选择一个世界。";
            ApplyModFilter(); UpdateControls(); return;
        }
        try
        {
            var world = CurrentWorld; WorldTitle.Text = world.Name; WorldSubtitle.Text = world.Detail + " · " + world.Mode; CavesStatus.Text = world.HasCaves ? "未启动" : "未启用洞穴"; CavesAddress.IsEnabled = world.HasCaves; CavesTunnel.IsEnabled = world.HasCaves;
            settings.Profiles.TryGetValue(world.Path, out var profile);
            MasterTunnel.SelectedValue = profile?.MasterTunnelId;
            CavesTunnel.SelectedValue = world.HasCaves ? profile?.CavesTunnelId : null;
            CreationScope.SelectedIndex = profile?.PublicServer == true ? 1 : 0;
            RestoreWorldNode(profile);
            ResetWorldRules();
            RecoveryButton.Visibility = profile?.PendingTunnelShards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RetryNodeChange.Visibility = profile?.NodeChange is not null || profile?.PendingTunnelShards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateTunnelFields();
            ResetLiveStatus();
            LoadWorldCredentials(world);
            modRefreshTask = RefreshModManagerAsync(world);
            await modRefreshTask;
            if (CurrentWorld?.Path != world.Path) return;
            Persist(); UpdateControls();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void UpdateTunnelFields()
    {
        MasterAddress.IsReadOnly = CavesAddress.IsReadOnly = true;
        settings.Profiles.TryGetValue(CurrentWorld?.Path ?? "", out var profile);
        if (profile?.NodeChange is not null) { MasterAddress.Text = CavesAddress.Text = ""; return; }
        MasterAddress.Text = (MasterTunnel.SelectedItem as TunnelInfo)?.ConnectionAddress ?? profile?.MasterAddress ?? "";
        CavesAddress.Text = CurrentWorld?.HasCaves == true ? (CavesTunnel.SelectedItem as TunnelInfo)?.ConnectionAddress ?? profile?.CavesAddress ?? "" : "";
        MasterAddress.ToolTip = CavesAddress.ToolTip = "一键开服会读取并启动当前世界的樱花通道；地址由所选通道自动生成";
    }
    private void Tunnel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        UpdateTunnelFields();
        if ((sender as ComboBox)?.SelectedItem is TunnelInfo tunnel)
            StatusText.Text = tunnel.ConnectionAddress.Length > 0 ? "已自动生成连接地址；开服时会核对 UDP 类型、节点和端口。" : $"节点 {tunnel.Node} 缺少连接域名，请重新读取隧道后再开服。";
    }
    private LaunchPlan BuildPlan()
    {
        var world = RequireWorld();
        if (settings.Profiles.GetValueOrDefault(world.Path)?.NodeChange is not null) throw new InvalidOperationException("节点更换尚未完成，请先继续配置节点，完成后再开服。");
        var master = MasterTunnel.SelectedItem as TunnelInfo ?? throw new InvalidOperationException("地上隧道尚未配置完成，请在联机设置中选择节点并保存。");
        var caves = world.HasCaves ? CavesTunnel.SelectedItem as TunnelInfo ?? throw new InvalidOperationException("洞穴隧道尚未配置完成，请重新保存联机设置。") : null;
        if (caves is not null && (caves.Node != master.Node || caves.Id == master.Id)) throw new InvalidOperationException("地上与洞穴隧道不在同一节点或编号重复，请检查樱花面板中的当前世界隧道。");
        UpdateTunnelFields();
        return new(world, ServerPath.Text.Trim(), WorkshopPath.Text.Trim(), master.GetEndpoint(), caves?.GetEndpoint());
    }
    private async void LoadTunnels_Click(object sender, RoutedEventArgs e) => await Guard(() => LoadTunnelsAsync());
    private async Task LoadTunnelsAsync(bool requireSelection = false)
    {
        var world = RequireWorld();
        settings.Profiles.TryGetValue(world.Path, out var profile);
        profile ??= new();
        var masterId = (MasterTunnel.SelectedItem as TunnelInfo)?.Id ?? profile.MasterTunnelId;
        var cavesId = (CavesTunnel.SelectedItem as TunnelInfo)?.Id ?? profile.CavesTunnelId;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var tunnels = await new SakuraClient(http).TunnelsAsync(SakuraToken.Password.Trim());
        var udp = tunnels.Where(t => t.Type.Equals("udp", StringComparison.OrdinalIgnoreCase)).ToArray();
        MasterTunnel.ItemsSource = udp; CavesTunnel.ItemsSource = udp;
        MasterTunnel.SelectedValue = masterId; CavesTunnel.SelectedValue = world.HasCaves ? cavesId : null;
        // Older profiles may have only addresses; interrupted creation may have only ownership notes.
        TunnelInfo? Restore(string shard, int? id, string address)
        {
            if (id.HasValue) return udp.FirstOrDefault(t => t.Id == id);
            var matches = Guid.TryParseExact(profile.CreationKey, "N", out _)
                ? udp.Where(t => t.Note == $"SakuraCamp:{profile.CreationKey}:{shard}").ToArray()
                : udp.Where(t => address.Length > 0 && t.ConnectionAddress.Equals(address, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("当前世界的隧道记录不唯一，请先检查樱花面板中的重复隧道。");
            return matches.SingleOrDefault();
        }
        MasterTunnel.SelectedValue = Restore("Master", masterId, profile.MasterAddress)?.Id;
        CavesTunnel.SelectedValue = world.HasCaves ? Restore("Caves", cavesId, profile.CavesAddress)?.Id : null;
        if (MasterTunnel.SelectedItem is TunnelInfo restoredMaster && CreationNode.SelectedItem is null)
            RestoreWorldNode(profile, restoredMaster.Node, restoredMaster.NodeHost);
        if (requireSelection)
        {
            var pair = SakuraClient.SelectWorldTunnels(udp, profile, world.HasCaves, masterId, cavesId);
            MasterTunnel.SelectedValue = pair.Master.Id; CavesTunnel.SelectedValue = pair.Caves?.Id;
        }
        UpdateTunnelFields();
        if (RememberToken.IsChecked == true) SecretStore.Save(SecretPath, SakuraToken.Password.Trim()); else if (File.Exists(SecretPath)) File.Delete(SecretPath);
        StatusText.Text = udp.Length == 0 ? "账号没有 UDP 隧道，请先为当前世界创建隧道。" : $"读取到 {udp.Length} 条 UDP 隧道。一键开服会启动当前世界所选通道。";
    }
    private bool CreationIsPublic()
    {
        if (CreationScope.SelectedIndex < 0) throw new InvalidOperationException("请先选择朋友私下联机或公开招募服务器，以便筛选合适节点。");
        return CreationScope.SelectedIndex == 1;
    }
    private void CreationScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CreationNode is null) return;
        var previous = (CreationNode.SelectedItem as NodeInfo)?.Id;
        suppressNodeSelection++;
        try
        {
            CreationNode.ItemsSource = CreationScope.SelectedIndex < 0 ? Array.Empty<NodeInfo>()
                : creationNodes.Where(n => n.IsAvailable(CreationScope.SelectedIndex == 1)).OrderBy(n => n.Vip).ThenBy(n => n.Id).ToArray();
            CreationNode.SelectedValue = previous;
        }
        finally { suppressNodeSelection--; }
    }
    private async void LoadNodes_Click(object sender, RoutedEventArgs e) => await Guard(LoadNodesAsync);
    private async Task LoadNodesAsync()
    {
        _ = RequireWorld(); var publicServer = CreationIsPublic();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        creationNodes = await new SakuraClient(http).NodesAsync(SakuraToken.Password.Trim());
        var available = creationNodes.Where(n => n.IsAvailable(publicServer)).OrderBy(n => n.Vip).ThenBy(n => n.Id).ToArray();
        var selectedId = (CreationNode.SelectedItem as NodeInfo)?.Id;
        settings.Profiles.TryGetValue(RequireWorld().Path, out var profile);
        RestoreWorldNode(profile, selectedId ?? profile?.CreationNodeId ?? (MasterTunnel.SelectedItem as TunnelInfo)?.Node);
        StatusText.Text = available.Length > 0 ? $"找到 {available.Length} 个可创建 UDP 的节点。请选择地区和账号权限合适的节点。" : "当前没有符合用途的可用节点。请检查账号权限或稍候重试。";
    }
    private async void CreateTunnels_Click(object sender, RoutedEventArgs e) => await Guard(CreateTunnelsAsync);
    private async Task CreateTunnelsAsync()
    {
        RequireIdle(); var world = RequireWorld(); var publicServer = CreationIsPublic();
        var node = CreationNode.SelectedItem as NodeInfo ?? throw new InvalidOperationException("请先读取可用节点并选择一个节点。");
        if (!node.IsAvailable(publicServer)) throw new InvalidOperationException("请先点击读取节点，确认已保存节点当前可用后再创建隧道。");
        var token = SakuraToken.Password.Trim();
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("请先填写樱花访问密钥。");
        if (!settings.Profiles.TryGetValue(world.Path, out var profile)) settings.Profiles[world.Path] = profile = new();
        if (profile.CreationNodeId is not null && profile.CreationNodeId != node.Id)
            throw new InvalidOperationException($"此世界记录的创建节点为 {profile.CreationNodeId}。若上次没有创建成功，请先点“恢复未完成的隧道配置”，再选择节点重试；已有隧道请选回原节点继续。");
        if (profile.CreationKey.Length == 0) profile.CreationKey = Guid.NewGuid().ToString("N");
        profile.CreationNodeId = node.Id; profile.SavedNode = node; profile.PublicServer = publicServer;
        Persist(); // Persist ownership and pending requests before any remote mutation.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var api = new SakuraClient(http);
        var pair = await api.EnsureWorldTunnelsAsync(token, node.Id, publicServer, profile.CreationKey, world.HasCaves,
            line => { AppendLog(LogReader.Redact(line, token)); StatusText.Text = LogReader.Redact(line, token); }, profile.PendingTunnelShards, Persist);
        profile.MasterTunnelId = pair.Master.Id; profile.CavesTunnelId = pair.Caves?.Id;
        profile.AutomaticTunnels = true;
        profile.MasterAddress = pair.Master.ConnectionAddress; profile.CavesAddress = pair.Caves?.ConnectionAddress ?? "";
        Persist();
        var tunnels = await api.TunnelsAsync(token);
        MasterTunnel.ItemsSource = tunnels.Where(t => t.Type == "udp").ToArray();
        CavesTunnel.ItemsSource = tunnels.Where(t => t.Type == "udp").ToArray();
        MasterTunnel.SelectedValue = pair.Master.Id; CavesTunnel.SelectedValue = pair.Caves?.Id;
        UpdateTunnelFields();
        if (RememberToken.IsChecked == true) SecretStore.Save(SecretPath, token);
        StatusText.Text = world.HasCaves ? "地上和洞穴隧道已创建或恢复，方案已保存。可直接点击一键开服。" : "地上隧道已创建或恢复，方案已保存。可直接点击一键开服。";
    }
    private async void RecoverCreation_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        RequireIdle(); var world = RequireWorld();
        if (!settings.Profiles.TryGetValue(world.Path, out var profile))
            throw new InvalidOperationException("当前世界没有创建记录，请直接读取节点并创建隧道。");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var api = new SakuraClient(http); var token = SakuraToken.Password.Trim();
        var recovery = await api.RecoverCreationAsync(token, profile, false, Persist);
        if (recovery.ConfirmationRequired)
        {
            var names = string.Join("\n", recovery.MissingNames);
            if (MessageBox.Show(this, $"重新读取后，接口列表中没有以下待确认隧道：\n{names}\n\n请确认你已在同一樱花账号的官方面板核对，且上次请求已结束。\n确认没有这些隧道后，是否解除待确认状态？随后可重新创建。\n若列表尚未同步，过早解除可能造成重复创建。", "核对未创建的隧道", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { StatusText.Text = "已保留待确认状态，可先去樱花官方面板核对。"; return; }
            recovery = await api.RecoverCreationAsync(token, profile, true, Persist);
        }
        var text = recovery.NodeUnlocked
            ? "未找到此世界对应的隧道，已解除待确认状态和节点限制。可选择节点，再点保存联机设置。"
            : $"找到 {recovery.FoundCount} 条相关隧道，已解除待确认状态。请选回节点 {profile.CreationNodeId}，再点保存联机设置继续配置。";
        RecoveryButton.Visibility = profile.PendingTunnelShards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = text; AppendLog(text);
    });
    private bool startingWorld;
    private async void Start_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        startingWorld = true; UpdateControls();
        try { await StartWorldAndTunnelsAsync(); }
        finally { startingWorld = false; }
    });
    private async Task StartWorldAndTunnelsAsync()
    {
        RequireIdle();
        if (string.IsNullOrWhiteSpace(SakuraToken.Password))
            throw new InvalidOperationException("一键开服会自动启动樱花通道，请在联机设置中填写樱花访问密钥，保存后会在本机加密记住密钥。不会在通道未启动时单独启动世界。");
        await EnsureServerVersionAsync(false);
        await SavePendingWorldRulesAsync();
        StatusText.Text = "正在恢复当前世界的隧道方案…";
        await EnsureConnectionTunnelsAsync();
        var plan = BuildPlan(); plan.Validate(); SaveProfile();
        var password = WorldFiles.Password(plan.World);
        var token = File.ReadAllText(Path.Combine(plan.World.Path, "cluster_token.txt")).Trim();
        session.SetSecrets(password, token, SakuraToken.Password.Trim());
        var selected = new List<TunnelInfo> { (TunnelInfo)MasterTunnel.SelectedItem! };
        if (plan.World.HasCaves) selected.Add((TunnelInfo)CavesTunnel.SelectedItem!);
        var useLauncher = await Task.Run(() => SakuraLauncherService.IsAvailable);
        if (!useLauncher)
        {
            if (System.Diagnostics.Process.GetProcessesByName("SakuraFrpService").Length > 0)
                throw new InvalidOperationException("樱花服务正在运行但本地控制接口不可用，请更新或重启官方客户端后重试。");
            foreach (var tunnel in selected)
                if (tunnel.Online) throw new InvalidOperationException("所选通道已在其他客户端运行，请先关闭该处连接再开服。");
            await EnsureFrpcAsync();
        }
        BeginTunnelLog();
        activeTunnelIds = selected.Select(t => t.Id).ToArray(); activeTunnelToken = SakuraToken.Password.Trim();
        AppendLog($"一键开服：自动启动 {selected.Count} 条樱花 UDP 通道：{string.Join("、", selected.Select(t => t.Name))}。");
        AppendLog(useLauncher ? "[樱花] 通过官方客户端服务打开通道开关，运行状态会同步至樱花客户端。" : "[樱花] 未安装官方客户端服务，使用独立 frpc 运行通道。");
        await LoadAvatarsAsync();
        liveNodeHost = plan.Master.Host;
        ResetLiveStatus();
        MasterStatus.Text = "准备启动"; CavesStatus.Text = plan.World.HasCaves ? "准备启动" : "未启用洞穴";
        TunnelStatus.Text = "正在自动启动";
        ShowTunnelHealth("等待通道启动及官方在线确认");
        StatusText.Text = "正在备份并自动启动樱花通道；通道就绪后启动世界…";
        async Task VerifyOnline()
        {
            TunnelStatus.Text = "核验在线状态";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            await new SakuraClient(http).WaitForOnlineAsync(activeTunnelToken, activeTunnelIds,
                progress: message => AppendLog("[通道核验] " + message));
            ShowTunnelHealth(plan.World.HasCaves ? "地上 在线 · 洞穴 在线（官方核验）" : "地上 在线（官方核验）");
            TunnelStatus.Text = "全部在线";
        }
        if (useLauncher)
        {
            var launcher = new SakuraLauncherService();
            var lease = new LauncherTunnelLease(launcher, selected, AppendLog);
            try
            {
                await session.StartWithLauncherAsync(plan, store.BackupRoot, () => lease.StartAsync(),
                    async () => { await lease.StopAsync(); launcher.Dispose(); }, VerifyOnline);
            }
            catch { if (!session.HasProcesses) launcher.Dispose(); throw; }
        }
        else
        {
            var frpc = SakuraClient.StartInfo(FrpcPath.Text.Trim(), SakuraToken.Password.Trim(), selected.Select(t => t.Id));
            await session.StartWithTunnelsAsync(plan, store.BackupRoot, frpc, verifyOnline: VerifyOnline);
        }
        StatusText.Text = "樱花通道与服务器均已就绪。可复制命令加入。";
        await RefreshLiveStatusAsync();
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await Guard(async () => { StatusText.Text = "正在保存世界，请稍候…"; StatusText.Text = await session.StopAsync() ? "已保存关服。" : "保存退出尚未完成，请查看日志。"; });
    private async void Check_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var plan = BuildPlan(); plan.Validate(); WorldFiles.RequireStopped();
        var missing = mods.Count(m => !m.Downloaded);
        if (missing > 0)
        {
            StatusText.Text = $"配置通过，但有 {missing} 个 Mod 文件未找到。";
        }
        else
        {
            StatusText.Text = "✅ 全部检查通过，可以开服了";
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5E, 0xD0, 0x7A));
            AppendLog("✅ 全部条件通过：配置、存档、令牌、Mod 均已就绪，可以开服。");
            MessageBox.Show(this, "所有开服条件检查通过，可以开服了。\n\n• 配置正确\n• 存档完整\n• 令牌有效\n• Mod 已就绪", "检查通过", MessageBoxButton.OK, MessageBoxImage.Information);
            // 恢复默认前景色
            StatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE4, 0xE6, 0xEB));
        }
        await Task.CompletedTask;
    });
    private async void SaveProfile_Click(object sender, RoutedEventArgs e) => await Guard(() => { _ = BuildPlan(); SaveProfile(); StatusText.Text = "已保存此世界的联机方案。"; return Task.CompletedTask; });
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await Guard(async () => { RequireIdle(); Persist(); await ScanAsync(); });
    private async void Import_Click(object sender, RoutedEventArgs e) => await Guard(async () => { RequireIdle(); var dialog = new OpenFolderDialog { Title = "选择包含 cluster.ini 的世界文件夹" }; if (dialog.ShowDialog() != true) return; var imported = Discovery.ReadWorld(dialog.FolderName); if (!Directory.Exists(Path.Combine(imported.Path, "Master"))) throw new InvalidOperationException("请选择含 Master 文件夹的本地世界。"); RootPath.Text = Directory.GetParent(imported.Path)!.FullName; await ScanAsync(imported.Path); });
    private async void SaveWorld_Click(object sender, RoutedEventArgs e) => await Guard(async () => { RequireIdle(); var world = RequireWorld(); var password = WorldPassword.Password; var token = KleiToken.Password.Trim(); if (token.Length > 0 && !LaunchPlan.IsToken(token)) throw new InvalidOperationException("请粘贴完整的 pds- 开头的 Klei 独立服务器令牌。"); var iniPath = Path.Combine(world.Path, "cluster.ini"); var ini = IniDocument.Load(iniPath); var backup = await Task.Run(() => WorldFiles.Backup(world.Path, store.BackupRoot)); ini.Set("NETWORK", "cluster_password", password); ini.Save(iniPath); if (token.Length > 0) AtomicFile.Write(Path.Combine(world.Path, "cluster_token.txt"), token); KleiToken.Clear(); AppendLog("世界设置已保存，备份：" + backup); await ScanAsync(world.Path); });
    private async void Backup_Click(object sender, RoutedEventArgs e) => await Guard(async () => { RequireIdle(); var source = RequireWorld().Path; var path = await Task.Run(() => WorldFiles.Backup(source, store.BackupRoot)); AppendLog("完整备份：" + path); StatusText.Text = "备份完成。"; });
    private async void CopyFriend_Click(object sender, RoutedEventArgs e) => await CopyConnect(false);
    private async void CopyHost_Click(object sender, RoutedEventArgs e) => await CopyConnect(true);
    private Task CopyConnect(bool local) => ReadOnlyAction(async () =>
    {
        var world = RequireWorld(); var ep = Endpoint.Parse(MasterAddress.Text);
        var password = await Task.Run(() => WorldFiles.Password(world));
        CopyText(WorldFiles.Connect(local ? "127.0.0.1" : ep.Host, ep.Port, password));
        StatusText.Text = "已复制连接命令（包含密码）。";
    });
    private async void CopyMods_Click(object sender, RoutedEventArgs e) => await ReadOnlyAction(() => {
        var snapshot = modSnapshot ?? throw new InvalidOperationException("本地 Mod 配置尚未就绪，请等待自动更新后重试。");
        WorldMods.CheckCurrent(snapshot);
        CopyText(WorldMods.SyncList(installedMods, snapshot)); StatusText.Text = "已复制当前世界启用的 Mod 清单和工坊链接。"; return Task.CompletedTask;
    });
    private async void RefreshMods_Click(object sender, RoutedEventArgs e) => await Guard(async () => { if (CurrentWorld is { } world) await RefreshModManagerAsync(world); });
    private async void ImportLog_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var dialog = new OpenFileDialog { Filter = "日志文件|*.txt;*.log|所有文件|*.*", Title = "选择朋友最近一次失败后的 client_log.txt" };
        if (dialog.ShowDialog() != true) return;
        var secrets = new[] { WorldPassword.Password, KleiToken.Password, SakuraToken.Password };
        AppendLog("开始完整导入客户端日志：" + dialog.FileName);
        await Task.Run(async () =>
        {
            using var input = new StreamReader(dialog.FileName, Encoding.UTF8, true);
            while (await input.ReadLineAsync() is { } line) QueueSessionLog("[客户端导入] " + LogReader.Redact(line, secrets));
        });
        AppendLog("客户端日志诊断：\n" + string.Join("\n", LogReader.Diagnose(LogReader.Tail(dialog.FileName, 512 * 1024))));
        StatusText.Text = "已完整导入脱敏日志；界面显示最近内容，导出保留全部导入记录。";
    });
    private async void ExportLog_Click(object sender, RoutedEventArgs e) => await ReadOnlyAction(async () => { DrainPendingLogs(int.MaxValue); var dialog = new SaveFileDialog { FileName = "SakuraCamp-diagnostics.txt", Filter = "文本文件|*.txt" }; if (dialog.ShowDialog() == true) { await ExportDiagnosticsAsync(dialog.FileName); StatusText.Text = "已导出本次程序启动以来的完整脱敏日志（截至点击导出时）。"; } });
    private void ClearLog_Click(object sender, RoutedEventArgs e) { pendingLogs.Clear(); pendingTunnelLogs.Clear(); logsDirty = tunnelLogsDirty = false; logLines.Clear(); LogBox.Clear(); tunnelLines.Clear(); TunnelLogBox.Clear(); }
    private void AppendLog(string line)
    {
        RecordDiagnostic(LogReader.Redact(line, SakuraToken.Password, KleiToken.Password, WorldPassword.Password));
        AppendLogToView(line);
    }
    private void AppendLogToView(string line)
    {
        line = LogReader.Redact(line, SakuraToken.Password, KleiToken.Password, WorldPassword.Password);
        var formatted = $"{DateTime.Now:HH:mm:ss}  {line}";
        logLines.Enqueue(formatted); while (logLines.Count > 600) logLines.Dequeue();
        logsDirty = true;
        if (IsTunnelLog(line)) AppendTunnelLog(formatted);
    }
    private async Task Guard(Func<Task> action) { if (busy) return; busy = true; UpdateControls(); try { await action(); } catch (Exception ex) { ShowError(ex); } finally { busy = false; UpdateControls(); } }
    private void ShowError(Exception ex) { var message = LogReader.Redact(ex.Message, SakuraToken.Password, KleiToken.Password, WorldPassword.Password); StatusText.Text = message; AppendLog("提示：" + message); if (App.SmokeDirectory is not null) { File.WriteAllText(Path.Combine(App.SmokeDirectory, "error.txt"), message); return; } MessageBox.Show(this, message, "樱火营地", MessageBoxButton.OK, MessageBoxImage.Information); }
    private void UpdateControls() { if (!initialized) return; var running = session.HasProcesses; WorldList.IsEnabled = !busy && !running; ConnectPanel.IsEnabled = true; ConnectionFields.IsEnabled = !busy && !running; SettingsPanel.IsEnabled = !busy && !running; StartButton.IsEnabled = !busy && !running && CurrentWorld is not null; StopButton.IsEnabled = !busy && running; ApiPanel.IsEnabled = !busy && !running; StartButton.Content = startingWorld ? "开服中…" : "▶  一键开服"; UpdateTunnelFields(); UpdateModEditing(); UpdateRuleEditing(); }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (logsClosed) return;
        if (busy) { e.Cancel = true; StatusText.Text = "正在执行操作，请稍后再关闭。"; return; }
        e.Cancel = true;
        if (session.HasProcesses)
        {
            if (MessageBox.Show(this, "服务器正在运行。保存并关闭后退出工具？", "保存关服", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await Guard(async () => { if (await session.StopAsync()) { await FinishLogsAsync(); _ = Dispatcher.BeginInvoke(Close); } });
        }
        else { await FinishLogsAsync(); _ = Dispatcher.BeginInvoke(Close); }
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e) => PickFolder(RootPath, "选择 DoNotStarveTogether 存档目录");
    private void BrowseWorkshop_Click(object sender, RoutedEventArgs e) => PickFolder(WorkshopPath, "选择 steamapps\\workshop 目录");
    private void BrowseServer_Click(object sender, RoutedEventArgs e) => PickExe(ServerPath, "选择饥荒独立服务器程序");
    private void BrowseFrpc_Click(object sender, RoutedEventArgs e) => PickExe(FrpcPath, "选择樱花官方 frpc.exe");
    private async Task EnsureFrpcAsync(bool forceDownload = false)
    {
        var path = FrpcPath.Text.Trim();
        if (forceDownload || !File.Exists(path) || FrpcInstaller.IsLauncherCompanion(path))
        {
            StatusText.Text = "正在从樱花官方下载独立版 frpc 并核对校验值…";
            AppendLog("下载官方独立版 frpc；不修改官方启动器安装目录。");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            path = await FrpcInstaller.InstallAsync(http, Path.Combine(store.Root, "tools"));
            FrpcPath.Text = path; Persist();
        }
        await FrpcInstaller.ProbeAsync(path);
        AppendLog("官方独立版 frpc 启动兼容性检查通过。");
    }
    private async void DownloadFrpc_Click(object sender, RoutedEventArgs e) => await Guard(async () => { RequireIdle(); await EnsureFrpcAsync(true); StatusText.Text = "官方独立版 frpc 已就绪。"; });
    private static void PickFolder(TextBox target, string title) { var dialog = new OpenFolderDialog { Title = title }; if (dialog.ShowDialog() == true) target.Text = dialog.FolderName; }
    private static void PickExe(TextBox target, string title) { var dialog = new OpenFileDialog { Title = title, Filter = "程序|*.exe" }; if (dialog.ShowDialog() == true) target.Text = dialog.FileName; }
    private static void Open(string uri) { try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    private void SakuraPanel_Click(object sender, RoutedEventArgs e) => Open("https://www.natfrp.com/tunnel/");
    private void Klei_Click(object sender, RoutedEventArgs e) => Open("https://accounts.klei.com/account/game/servers?game=DontStarveTogether");
    private void OpenWorld_Click(object sender, RoutedEventArgs e) { if (CurrentWorld is not null) Open(CurrentWorld.Path); }
    private void OpenBackups_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(store.BackupRoot); Open(store.BackupRoot); }

    private async Task SmokeAsync(string directory)
    {
        try
        {
            // Offline fixture exercises the real selection events without reading account credentials.
            var fixtures = new[]
            {
                new TunnelInfo(1, "Master", 216, "udp", "127.0.0.1", 44812, "44812", false, 0) { NodeHost = "frp-example.com" },
                new TunnelInfo(2, "Caves", 216, "udp", "127.0.0.1", 26445, "26445", false, 0) { NodeHost = "frp-example.com" },
                new TunnelInfo(3, "Master alternate", 216, "udp", "127.0.0.1", 44813, "44813", false, 0) { NodeHost = "frp-example.com" }
            };
                if (ApiPanel.Visibility != Visibility.Visible) throw new InvalidOperationException("Tunnel controls must always be visible");
            MasterTunnel.ItemsSource = fixtures; CavesTunnel.ItemsSource = fixtures;
            MasterTunnel.SelectedValue = 1; CavesTunnel.SelectedValue = 2;
            if (MasterAddress.Text != "frp-example.com:44812" || CavesAddress.Text != "frp-example.com:26445" ||
                !MasterAddress.IsReadOnly)
                throw new InvalidOperationException("Automatic tunnel address UI regression");
            if (CurrentWorld is not null && BuildPlan().Master.Port != 44812) throw new InvalidOperationException("Automatic launch plan regression");
            MasterTunnel.SelectedValue = 3;
            if (MasterAddress.Text != "frp-example.com:44813") throw new InvalidOperationException("Tunnel selection left stale address");
            MasterTunnel.SelectedValue = 1;
            var automaticAddressVerified = true;
            creationNodes = new[]
            {
                new NodeInfo(1, "普通节点", "private.example.com", 0, 4 | 32),
                new NodeInfo(2, "公开游戏节点", "public.example.com", 0, 4 | 32 | 2048),
                new NodeInfo(3, "已满节点", "full.example.com", 0, 32),
                new NodeInfo(4, "离线节点", "offline.example.com", 0, 4 | 32 | 512)
            };
            CreationScope.SelectedIndex = -1; CreationScope.SelectedIndex = 0;
            if (CreationNode.Items.Count != 2) throw new InvalidOperationException("Private node filter UI regression");
            CreationScope.SelectedIndex = 1;
            if (CreationNode.Items.Count != 1 || ((NodeInfo)CreationNode.Items[0]).Id != 2)
                throw new InvalidOperationException("Public game node filter UI regression");
            CreationScope.SelectedIndex = 0; CreationNode.SelectedValue = 1;
            var profileFixture = new WorldProfile { MasterTunnelId = 1, CavesTunnelId = 2, AutomaticTunnels = true };
            var restoredProfile = System.Text.Json.JsonSerializer.Deserialize<WorldProfile>(System.Text.Json.JsonSerializer.Serialize(profileFixture))!;
            if (restoredProfile.AutomaticTunnels != true || restoredProfile.CavesTunnelId != 2) throw new InvalidOperationException("Saved automatic mode regression");
            restoredProfile.AutomaticTunnels = false;
            var legacyPair = SakuraClient.SelectWorldTunnels(fixtures, restoredProfile, true);
            if (legacyPair.Master.Id != 1 || legacyPair.Caves?.Id != 2 || ApiPanel.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Legacy manual profile must still resolve both tunnels for one-click startup");
            // Demonstration data only; never query a real account or start a real server in smoke mode.
            if (liveTimer.Interval != TimeSpan.FromSeconds(1) || FindName("RefreshStatusButton") is not null)
                throw new InvalidOperationException("Status must automatically update every second without a refresh button");
            BeginTunnelLog();
            AppendLog("[樱花] 离线界面演示：独立 frpc 已启动。");
            for (var logIndex = 0; logIndex < 605; logIndex++) AppendLog("离线日志保留检查 " + logIndex);
            await CompleteTunnelFileForSmokeAsync();
            if (!string.Join("\n", tunnelLines).Contains("独立 frpc") || string.Join("\n", logLines).Contains("独立 frpc") || !File.ReadAllText(tunnelLogPath!).Contains("独立 frpc"))
                throw new InvalidOperationException("Tunnel log must survive game log rollover and persist to disk");
            logLines.Clear(); LogBox.Clear();
            ShowTunnelHealth("地上 在线 · 洞穴 在线（离线演示）");
            nodeProbeText = "28 ms · ICMP";
            var demoSample = new SessionStatus(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(35), new[]
            {
                new ShardStatus("Master", true, new(DateTimeOffset.UtcNow, 12, "autumn", false,
                    new[] { new PlayerStatus("KU_demo_1", "营地玩家 🌸", "wilson", "Master", null, true) { NetworkScore = 0, Health = new(128, 150), Hunger = new(106, 150), Sanity = new(175, 200) } }), "实时数据已更新", 4.2, 320 * 1024 * 1024),
                new ShardStatus("Caves", true, new(DateTimeOffset.UtcNow, 12, "autumn", false,
                    new[] { new PlayerStatus("KU_demo_2", "洞穴探险家", "wendy", "Caves", null, true) { NetworkScore = 1, Health = new(95, 150), Hunger = new(64, 150), Sanity = new(82, 200) } }), "实时数据已更新", 2.1, 280 * 1024 * 1024)
            });
            RenderLiveStatus(demoSample);
            if (LivePlayers.Items.Count != 2 || !LivePlayerCount.Text.StartsWith("2 /")) throw new InvalidOperationException("Live status UI regression");
            var demoCard = playerCards.Single(p => p.UserId == "KU_demo_1");
            RenderLiveStatus(demoSample);
            if (!ReferenceEquals(demoCard, playerCards.Single(p => p.UserId == "KU_demo_1")) || demoCard.Health != "128 / 150" || demoCard.CharacterName != "威尔逊" || demoCard.PingText != "网络良好")
                throw new InvalidOperationException("Player card identity, vitals or network label regression");
            if (characterAvatars.Count > 0 && demoCard.Avatar is null) throw new InvalidOperationException("Installed game portrait missing");
            LiveUpdated.Text = "离线界面验证 · 以下为演示数据";
            if (installedMods.Any(m => m.ClientOnly) || modCards.Count != installedMods.Count) throw new InvalidOperationException("Client mods must not appear in Mod management");
            if (modSnapshot is null) throw new InvalidOperationException("World Mod config unavailable in smoke");
            foreach (var card in modCards) card.SetEditable(true);
            var sampleMod = modCards.FirstOrDefault(c => c.Mod.Downloaded && c.Mod.Error.Length == 0 && c.Mod.Options.Count(o => !o.IsHeading && o.Choices.Count > 1) >= 2)
                ?? throw new InvalidOperationException("No real configurable local server Mod found");
            ModSearch.Text = sampleMod.Name; modSearchFilter.Flush();
            if (ModList.Items.Count == 0) throw new InvalidOperationException("Mod card search failed");
            ModSearch.Clear(); modSearchFilter.Flush();
            ModFilter.SelectedIndex = 1;
            if (ModList.Items.Cast<ModCard>().Any(c => !c.Enabled)) throw new InvalidOperationException("Enabled Mod filter failed");
            ModFilter.SelectedIndex = 0;
            var configDialog = new ModSettingsPanel(sampleMod.Mod, modSnapshot, (_, _) => throw new InvalidOperationException("Smoke must not write user Mod configs"));
            Tabs.SelectedItem = ModManagerTab;
            var configOverlayTask = ShowModSettingsAsync(configDialog); configDialog.CheckSmoke();
            VerifyModTooltipSmoke(configDialog, false);
            if (Application.Current.Windows.Count != 1) throw new InvalidOperationException("Mod settings created another window");
            await Task.Delay(250); configDialog.UpdateLayout();
            var configBitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)configDialog.ActualWidth, (int)configDialog.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            configBitmap.Render(configDialog);
            var configEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); configEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(configBitmap));
            using (var configStream = File.Create(Path.Combine(directory, "mod-settings.png"))) configEncoder.Save(configStream);
            if (ModOverlayContent.Clip is not System.Windows.Media.RectangleGeometry clip || clip.FillContains(new Point(1, 1)) || !clip.FillContains(new Point(20, 20))) throw new InvalidOperationException("Overlay content did not clip its square corners");
            SaveSmokeScreenshot(Path.Combine(directory, "mod-overlay.png"));
            configDialog.Dismiss(); await configOverlayTask;
            // Exercise the actual settings Save button against an isolated world, never the selected user world.
            var fixtureWorldPath = Path.Combine(directory, "mod-save-fixture");
            Directory.CreateDirectory(Path.Combine(fixtureWorldPath, "Master"));
            File.WriteAllText(Path.Combine(fixtureWorldPath, "cluster.ini"), "[NETWORK]\ncluster_name=Temporary Mod UI fixture\n");
            File.WriteAllText(Path.Combine(fixtureWorldPath, "Master", "modoverrides.lua"), "return {}");
            var fixtureWorld = Discovery.ReadWorld(fixtureWorldPath);
            var fixtureSnapshot = WorldMods.Read(fixtureWorld);
            var saveInvoked = false;
            var saveDialog = new ModSettingsPanel(sampleMod.Mod, fixtureSnapshot, async (changes, shards) =>
            {
                if (changes.Count != 1 || shards.Length != 1 || shards[0] != "Master") throw new InvalidOperationException("Mod UI save produced unexpected edits");
                await Task.Run(() => WorldMods.Apply(fixtureWorld, fixtureSnapshot, sampleMod.Mod, null, changes, shards, Path.Combine(directory, "mod-backup-fixture")));
                var persisted = WorldMods.Read(fixtureWorld).Files[0].Entry(sampleMod.Mod.Key);
                if (persisted.Enabled || changes.Any(p => persisted.Options.GetValueOrDefault(p.Key) != p.Value)) throw new InvalidOperationException("Mod UI save values did not round-trip");
                saveInvoked = true;
            });
            var saveOverlayTask = ShowModSettingsAsync(saveDialog);
            await saveDialog.SaveSmokeAsync(); await saveOverlayTask;
            if (!saveInvoked) throw new InvalidOperationException("Mod UI save callback not invoked");
            await VerifyNodeSelectionSmokeAsync();
            VerifySavedSettingsSmoke(directory);
            await VerifyConnectionLayoutSmokeAsync(directory);
            await VerifyDarkScrollingSmokeAsync(directory);
            await VerifyMotionSmokeAsync(directory);
            await VerifyStartupResponsivenessSmokeAsync(directory);
            await VerifyDropdownAndLogThumbSmokeAsync(directory);
            await VerifyWorldRulesSmokeAsync(directory);
            await VerifyModWatchingSmokeAsync(directory);
            Tabs.SelectedItem = ModManagerTab; UpdateLayout();
            VerifyModTooltipSmoke(ModList, true);
            var gearButtons = VisualChildren<Button>(ModList).Where(b => b.ToolTip as string == "配置 Mod").ToArray();
            if (gearButtons.Length == 0 || gearButtons.Any(b => ToolTipService.GetInitialShowDelay(b) != 300 || ToolTipService.GetBetweenShowDelay(b) != 0)) throw new InvalidOperationException("Gear tooltip delay regression");
            if (MasterTunnel.IsVisible || CavesTunnel.IsVisible || MasterAddress.IsVisible || CavesAddress.IsVisible) throw new InvalidOperationException("Automatic tunnel fields still visible");
            for (var index = 0; index < Tabs.Items.Count; index++)
            {
                Tabs.SelectedIndex = index; await Task.Delay(250); UpdateLayout();
                if (Tabs.SelectedItem == RulesTab) { await ruleLoadTask; UpdateLayout(); }
                var tabOrigin = Tabs.TranslatePoint(new Point(), this);
                if (Math.Abs(tabOrigin.X - 28) > 1 || Math.Abs(tabOrigin.Y - 109) > 1) throw new InvalidOperationException("Navigation moved between pages");
                if ((StartButton.IsVisible || MasterStatus.IsVisible) && Tabs.SelectedItem != ConnectionTab) throw new InvalidOperationException("Connection controls leaked into a function page");
                if (Tabs.SelectedItem == LiveTab) { FindVisualParent<ScrollViewer>(LivePlayers)?.ScrollToBottom(); await Task.Delay(100); UpdateLayout(); }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(this);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, $"tab-{index}.png")); encoder.Save(stream);
            }
            Width = MinWidth; Height = MinHeight;
            foreach (var tab in new[] { OverviewTab, ConnectionTab, RulesTab, ModManagerTab })
            {
                Tabs.SelectedItem = tab; await Task.Delay(150); UpdateLayout();
                SaveSmokeScreenshot(Path.Combine(directory, $"minimum-{Tabs.SelectedIndex}.png"));
            }
            var testSecretPath = Path.Combine(directory, "test-secret.dpapi");
            SecretStore.Save(testSecretPath, "smoke-test-secret");
            if (SecretStore.Load(testSecretPath) != "smoke-test-secret") throw new InvalidOperationException("DPAPI round-trip failed");
            File.Delete(testSecretPath);
            var report = new { Worlds = WorldList.Items.Count, Selected = CurrentWorld?.Folder, Mods = mods.Count, ServerFound = File.Exists(ServerPath.Text), FrpcFound = File.Exists(FrpcPath.Text), Tabs = Tabs.Items.Count, Dpapi = true, AutomaticAddressVerified = automaticAddressVerified, CreationNodeFilterVerified = true, OneClickAlwaysStartsTunnels = true, LegacyManualProfileVerified = true, LiveStatusVerified = true, OneSecondUpdates = true, RefreshButtonRemoved = true, PersistentTunnelLogVerified = true, PlayerCardsVerified = true, PortraitsLoaded = characterAvatars.Count, ServerModCards = modCards.Count, ModImages = modCards.Count(c => c.Image is not null), ClientModsExcluded = true, ModConfigurationVerified = true, ModSearchAndFiltersVerified = true, ModSaveButtonVerified = saveInvoked, IntegratedModOverlay = true, IsolatedFunctionPages = true, GearTooltipDelayMs = 300, AutomaticModDetectionVerified = true, FixedNavigation = true, WorldCardSelection = true, MaskedInputsVisible = true, LongInputLayoutStable = true, RoundedOverlayContent = true, OnlyGearModTooltip = true, WorkshopIdHidden = true, SavedConnectionFieldsVerified = true, ErrorKeepsPage = true, AutomaticNodeSelectionVerified = true };
            File.WriteAllText(Path.Combine(directory, "report.json"), System.Text.Json.JsonSerializer.Serialize(report));
            Application.Current.Shutdown(File.Exists(Path.Combine(directory, "error.txt")) ? 1 : 0);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString()); Application.Current.Shutdown(1); }
    }
}
