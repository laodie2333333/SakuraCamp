using System.Text;

namespace SakuraCamp.Core;

// Observe only new output from the requested Steam app; old checks and Workshop jobs
// cannot establish that the current file validation has finished.
public sealed class SteamValidationMonitor
{
    private readonly string path, marker;
    private long position;
    private string pending = "";
    private bool appRunning, appStopped, completed;

    public SteamValidationMonitor(string logPath, string appId)
    {
        if (appId is not ("322330" or "343050")) throw new ArgumentException("不支持的 Steam 应用。", nameof(appId));
        path = logPath; marker = "AppID " + appId + " ";
        position = File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public bool Poll()
    {
        if (!File.Exists(path)) return false;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length < position) throw new InvalidOperationException("Steam 日志已轮换，无法确认本次验证结果。请等待 Steam 完成后重新检查服务端版本。");
        file.Position = position;
        var bytes = new byte[(int)Math.Min(file.Length - position, 2 * 1024 * 1024)];
        var count = file.Read(bytes); position += count;
        var text = pending + Encoding.UTF8.GetString(bytes, 0, count);
        var last = text.LastIndexOf('\n');
        if (last < 0) { pending = text.Length < 65536 ? text : ""; return completed; }
        pending = text[(last + 1)..];
        foreach (var line in text[..last].Split('\n'))
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) continue;
            var entry = line[(index + marker.Length)..].TrimEnd('\r');
            if (entry.StartsWith("App update changed : Running Update", StringComparison.Ordinal))
            { appRunning = true; appStopped = false; completed = false; }
            else if (appRunning && entry == "App update changed : None") appStopped = true;
            else if (entry.StartsWith("Workshop update changed", StringComparison.Ordinal))
            { appRunning = false; appStopped = false; }
            else if (appRunning && entry.StartsWith("scheduler finished", StringComparison.Ordinal))
            {
                if (!entry.Contains("(result No Error,", StringComparison.Ordinal))
                    throw new InvalidOperationException("Steam 更新或验证尚未成功完成，请在 Steam 中完成任务后重新检查服务端版本。" + entry);
                if (appStopped) { completed = true; appRunning = false; }
            }
        }
        return completed;
    }

    public async Task WaitAsync(string executable, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddHours(1);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Poll() && ServerVersions.Installation(executable).Complete) return;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new InvalidOperationException("等待 Steam 更新与验证超时，请在 Steam 中确认任务完成后重新检查服务端版本。");
    }
}
