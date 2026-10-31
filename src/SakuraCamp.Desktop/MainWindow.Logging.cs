using System.IO;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private readonly PendingLogs pendingLogs = new();
    private readonly PendingLogs pendingTunnelLogs = new(1024);
    private static bool IsTunnelLog(string line) => line.Contains("[樱花]", StringComparison.Ordinal) || line.Contains("[通道核验]", StringComparison.Ordinal) || line.StartsWith("一键开服：", StringComparison.Ordinal);
    private void QueueSessionLog(string line) { if (IsTunnelLog(line)) pendingTunnelLogs.Add(line); else pendingLogs.Add(line); }
    private readonly DispatcherTimer logTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
    private bool logsDirty, tunnelLogsDirty;
    private int logRenderCount;
    private Channel<string>? tunnelFileQueue;
    private Task tunnelFileTask = Task.CompletedTask;
    private readonly List<Task> previousTunnelFiles = [];
    private string? tunnelFileError;

    private void InitializeLogging()
    {
        logTimer.Tick += (_, _) => { DrainPendingLogs(); FlushLogViews(); };
        logTimer.Start();
        Closed += (_, _) => logTimer.Stop();
    }
    private void DrainPendingLogs(int maximum = 200)
    {
        var tunnelBatch = pendingTunnelLogs.Take(maximum, out var omittedTunnel);
        foreach (var line in tunnelBatch) AppendLog(line);
        if (omittedTunnel > 0) AppendLog($"[樱花] 通道输出较多，界面略过 {omittedTunnel} 条较早消息。");
        var lines = pendingLogs.Take(maximum, out var omitted);
        if (omitted > 0) AppendLog($"启动日志较多，界面略过 {omitted} 条较早输出，保留最近日志；就绪判定继续独立处理。");
        foreach (var line in lines) AppendLog(line);
    }
    private void FlushLogViews()
    {
        // Hidden pages keep the ring buffers but incur no text layout work.
        if (logsDirty && LogBox.IsVisible)
        {
            var offset = LogBox.VerticalOffset;
            var follow = offset >= LogBox.ExtentHeight - LogBox.ViewportHeight - 2;
            LogBox.Text = string.Join(Environment.NewLine, logLines); logsDirty = false; logRenderCount++;
            if (follow) LogBox.ScrollToEnd(); else LogBox.ScrollToVerticalOffset(offset);
        }
        if (tunnelLogsDirty && TunnelLogBox.IsVisible)
        {
            var offset = TunnelLogBox.VerticalOffset;
            var follow = offset >= TunnelLogBox.ExtentHeight - TunnelLogBox.ViewportHeight - 2;
            TunnelLogBox.Text = string.Join(Environment.NewLine, tunnelLines); tunnelLogsDirty = false;
            if (follow) TunnelLogBox.ScrollToEnd(); else TunnelLogBox.ScrollToVerticalOffset(offset);
        }
        if (Interlocked.Exchange(ref tunnelFileError, null) is not null)
            TunnelLogHint.Text = "通道日志无法写入文件，当前窗口仍保留输出。";
    }
    private void StartTunnelFile(string path)
    {
        tunnelFileQueue?.Writer.TryComplete(); previousTunnelFiles.Add(tunnelFileTask);
        var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
        tunnelFileQueue = queue;
        tunnelFileTask = Task.Run(async () =>
        {
            try
            {
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                await using var writer = new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true };
                await foreach (var line in queue.Reader.ReadAllAsync()) await writer.WriteLineAsync(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Interlocked.Exchange(ref tunnelFileError, ex.Message); queue.Writer.TryComplete(); }
        });
    }
    private bool logsClosed;
    private async Task FinishLogsAsync()
    {
        logTimer.Stop(); DrainPendingLogs(int.MaxValue); FlushLogViews();
        tunnelFileQueue?.Writer.TryComplete();
        await Task.WhenAll(previousTunnelFiles.Append(tunnelFileTask));
        logsClosed = true;
    }
    private async Task CompleteTunnelFileForSmokeAsync()
    {
        tunnelFileQueue?.Writer.TryComplete(); await tunnelFileTask;
    }
    private async Task ReadOnlyAction(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private Action<string>? clipboardSmokeAction;
    private void CopyText(string text)
    {
        if (clipboardSmokeAction is { } capture) capture(text);
        else System.Windows.Clipboard.SetText(text);
    }
}
