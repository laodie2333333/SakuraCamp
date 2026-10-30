using System.Net.NetworkInformation;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool refreshingLive;
    private bool windowClosed;
    private string liveNodeHost = "";
    private DateTimeOffset lastNodeProbe;
    private string nodeProbeText = "等待探测";
    private bool probingNode;
    private readonly ObservableCollection<PlayerCard> playerCards = [];
    private Dictionary<string, System.Windows.Media.ImageSource> characterAvatars = [];
    private string avatarExecutable = "";

    private async Task LoadAvatarsAsync()
    {
        var executable = ServerPath.Text;
        if (avatarExecutable == executable) return;
        avatarExecutable = executable;
        try { characterAvatars = await Task.Run(() => CharacterAvatars.Load(executable)); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Xml.XmlException or FormatException or ArgumentException or OverflowException or UnauthorizedAccessException)
        { characterAvatars = []; AppendLog("角色头像读取失败，将显示通用占位头像：" + ex.Message); }
        playerCards.Clear();
    }

    private void InitializeLiveStatus()
    {
        LivePlayers.ItemsSource = playerCards;
        liveTimer.Tick += async (_, _) => await RefreshLiveStatusAsync();
        Closed += (_, _) => { windowClosed = true; liveTimer.Stop(); };
        if (App.SmokeDirectory is null) liveTimer.Start();
    }

    private void ResetLiveStatus()
    {
        if (LivePlayers is null) return;
        playerCards.Clear();
        LivePlayerCount.Text = "—";
        LiveUptime.Text = "未运行";
        LiveWorldState.Text = "等待世界启动";
        LiveResources.Text = "等待采样";
        LiveShardDetail.Text = "开服后，每秒自动读取地上与洞穴的服务器状态。";
        LiveUpdated.Text = "尚未收到实时数据";
        LiveNodeLatency.Text = "等待探测";
        LiveEmpty.Text = "开服后将在这里显示在线玩家。";
        LiveEmpty.Visibility = Visibility.Visible;
        lastNodeProbe = default; nodeProbeText = "等待探测";
    }

    private async Task RefreshLiveStatusAsync()
    {
        if (refreshingLive || windowClosed) return;
        if (!session.IsReady)
        {
            ResetLiveStatus();
            if (session.HasProcesses)
            {
                LiveUptime.Text = "启动中 / 退出中";
                LiveUpdated.Text = "等待所有进程就绪；异常退出详情请查看日志。";
            }
            return;
        }
        refreshingLive = true;
        try
        {
            // Node ICMP has its own cadence; a slow reply must not hold up player updates.
            if (!probingNode) _ = ProbeNodeAsync();
            var status = await session.ReadStatusAsync(TimeSpan.FromMilliseconds(900));
            if (windowClosed || !session.IsReady) { ResetLiveStatus(); return; }
            RenderLiveStatus(status);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            LiveUpdated.Text = "读取暂时失败，等待下次刷新。";
        }
        finally { refreshingLive = false; }
    }

    private async Task ProbeNodeAsync()
    {
        if (liveNodeHost.Length == 0 || DateTimeOffset.UtcNow - lastNodeProbe < TimeSpan.FromSeconds(15)) return;
        probingNode = true;
        var host = liveNodeHost;
        lastNodeProbe = DateTimeOffset.UtcNow;
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2));
            if (host == liveNodeHost) nodeProbeText = reply.Status == IPStatus.Success ? $"{reply.RoundtripTime} ms · ICMP" : "节点未响应 ICMP";
        }
        catch (Exception ex) when (ex is PingException or System.Net.Sockets.SocketException or InvalidOperationException or ArgumentException)
        { if (host == liveNodeHost) nodeProbeText = "无法探测（节点可能禁用 ICMP）"; }
        finally { probingNode = false; }
    }

    private void RenderLiveStatus(SessionStatus status)
    {
        var valid = status.Shards.Where(s => s.Sample is not null).ToArray();
        var players = valid.SelectMany(s => s.Sample!.Players).GroupBy(p => p.UserId)
            .Select(g => g.OrderByDescending(p => p.InWorld).ThenByDescending(p => p.Shard == "Master").First())
            .OrderBy(p => p.Shard).ThenBy(p => p.Name).ToArray();
        var ids = players.Select(p => p.UserId).ToHashSet();
        foreach (var old in playerCards.Where(p => !ids.Contains(p.UserId)).ToArray()) playerCards.Remove(old);
        foreach (var player in players)
        {
            var card = playerCards.FirstOrDefault(p => p.UserId == player.UserId);
            if (card is null) playerCards.Add(new(player, characterAvatars));
            else card.Update(player);
        }
        var complete = status.Shards.Count > 0 && valid.Length == status.Shards.Count;
        LivePlayerCount.Text = complete ? $"{players.Length} / {session.World?.MaxPlayers ?? CurrentWorld?.MaxPlayers ?? 0} 人" : $"已读 {players.Length} 人 · 数据不完整";
        LiveUptime.Text = $"{(int)status.Uptime.TotalHours:00}:{status.Uptime.Minutes:00}:{status.Uptime.Seconds:00}";
        LiveNodeLatency.Text = nodeProbeText;
        var main = valid.FirstOrDefault(s => s.Shard == "Master")?.Sample;
        var seasons = new Dictionary<string, string> { ["autumn"] = "秋", ["winter"] = "冬", ["spring"] = "春", ["summer"] = "夏" };
        LiveWorldState.Text = main is null ? "地上数据暂不可用" : $"第 {main.Day} 天 · {seasons.GetValueOrDefault(main.Season, main.Season)} · {(main.Paused ? "已暂停" : "运行中")}";
        var cpu = status.Shards.Where(s => s.CpuPercent.HasValue).ToArray();
        var memory = status.Shards.Where(s => s.MemoryBytes.HasValue).ToArray();
        LiveResources.Text = $"CPU {(cpu.Length == 0 ? "采样中" : $"{cpu.Sum(s => s.CpuPercent!.Value):0.0}%")} · 内存 {(memory.Length == 0 ? "—" : $"{memory.Sum(s => s.MemoryBytes!.Value) / 1024d / 1024d:0} MB")}";
        LiveShardDetail.Text = string.Join("  ·  ", status.Shards.Select(s => $"{(s.Shard == "Master" ? "地上" : "洞穴")}：{(s.Running ? s.Detail : "进程已退出")}"));
        LiveUpdated.Text = $"{status.CapturedAt.ToLocalTime():HH:mm:ss} 更新 · 每秒自动更新 · {(complete ? "全部世界已响应" : "部分数据缺失，不能据此判断无人在线")}";
        LiveEmpty.Text = complete ? "当前没有在线玩家。" : "等待服务器返回玩家数据…";
        LiveEmpty.Visibility = players.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
