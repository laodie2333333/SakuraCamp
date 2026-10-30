using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer tunnelTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Queue<string> tunnelLines = new();
    private int[] activeTunnelIds = [];
    private string activeTunnelToken = "";
    private string tunnelHealthText = "尚未启动通道";
    private string? tunnelLogPath;
    private bool checkingTunnels;

    private void InitializeTunnelStatus()
    {
        tunnelTimer.Tick += async (_, _) => await RefreshTunnelHealthAsync();
        Closed += (_, _) => tunnelTimer.Stop();
        if (App.SmokeDirectory is null) tunnelTimer.Start();
    }

    private void BeginTunnelLog()
    {
        tunnelLines.Clear(); TunnelLogBox.Clear();
        var directory = Path.Combine(store.Root, "logs");
        Directory.CreateDirectory(directory);
        tunnelLogPath = Path.Combine(directory, $"tunnels-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
        TunnelLogHint.Text = "通道控制与核验日志保存在本机：" + tunnelLogPath;
    }

    private void AppendTunnelLog(string line)
    {
        tunnelLines.Enqueue(line);
        while (tunnelLines.Count > 200) tunnelLines.Dequeue();
        TunnelLogBox.Text = string.Join(Environment.NewLine, tunnelLines);
        TunnelLogBox.ScrollToEnd();
        if (tunnelLogPath is null) return;
        try { File.AppendAllText(tunnelLogPath, line + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { TunnelLogHint.Text = "通道日志无法写入文件，当前窗口仍保留输出。"; }
    }

    private void ShowTunnelHealth(string text, bool logChange = false)
    {
        if (windowClosed) return;
        if (logChange && tunnelHealthText != text) AppendLog("[通道核验] " + text);
        tunnelHealthText = text;
        TunnelDetail.Text = text;
        LiveTunnelSummary.Text = "樱花通道：" + text;
    }

    private async Task RefreshTunnelHealthAsync()
    {
        if (checkingTunnels || windowClosed || activeTunnelIds.Length == 0 || !session.HasProcesses) return;
        checkingTunnels = true;
        var ids = activeTunnelIds; var token = activeTunnelToken;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var tunnels = await new SakuraClient(http).TunnelStatesAsync(token);
            if (windowClosed || !ReferenceEquals(ids, activeTunnelIds) || !session.HasProcesses) return;
            var chosen = ids.Select(id => tunnels.SingleOrDefault(t => t.Id == id)).ToArray();
            var summary = string.Join(" · ", chosen.Select((t, index) => $"{(index == 0 ? "地上" : "洞穴")} {(t is null ? "已删除" : t.Status != 0 ? "不可用" : t.Online ? "在线" : "离线/重连中")}"));
            ShowTunnelHealth(summary + "（官方核验）", logChange: true);
            var allOnline = chosen.All(t => t is { Online: true, Status: 0 });
            TunnelStatus.Text = allOnline ? "全部在线" : "通道异常";
            if (!allOnline) StatusText.Text = "樱花通道异常：" + summary + "。世界仍在运行，请查看本工具的樱花通道日志。";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (ReferenceEquals(ids, activeTunnelIds) && session.HasProcesses)
            {
                ShowTunnelHealth("官方状态暂不可查，等待重新核验", logChange: true);
                TunnelStatus.Text = "待核验";
            }
        }
        finally { checkingTunnels = false; }
    }
}
