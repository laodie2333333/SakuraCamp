namespace SakuraCamp.Core;

public sealed class LauncherTunnelLease(ILauncherTunnelService service, IReadOnlyList<TunnelInfo> selected, Action<string>? log = null)
{
    private readonly List<int> enabledByUs = [];
    public IReadOnlyList<int> EnabledByUs => enabledByUs.ToArray();

    public async Task StartAsync(TimeSpan? timeout = null)
    {
        var ids = selected.Select(t => t.Id).ToArray();
        await service.RefreshAsync();
        IReadOnlyList<LauncherTunnel> initial;
        try { initial = await service.ReadTunnelsAsync(ids); }
        catch (OperationCanceledException ex) { throw new InvalidOperationException("官方客户端中未读到所选通道，请确认官方客户端和本工具登录的是同一个樱花账号。", ex); }
        foreach (var chosen in selected)
        {
            var local = initial.Single(t => t.Id == chosen.Id);
            if (local.Node != chosen.Node || local.LocalPort != chosen.LocalPort || local.LocalIp != chosen.LocalIp || local.Type != chosen.Type || local.Status != 0 || local.Remote != chosen.Remote)
                throw new InvalidOperationException($"官方客户端中的通道 {chosen.Name} 与所选方案不一致，请刷新客户端和当前世界的隧道。");
        }
        foreach (var tunnel in initial)
        {
            if (!tunnel.Enabled)
            {
                // Journal before the RPC so a lost response is still rolled back on failed startup.
                enabledByUs.Add(tunnel.Id);
                log?.Invoke($"[樱花] 正在打开官方客户端通道：{tunnel.Name}（{tunnel.Id}）。");
                await service.SetEnabledAsync(tunnel.Id, true);
            }
            else if (tunnel.State != 2)
            {
                log?.Invoke($"[樱花] 正在重试官方客户端已启用的通道：{tunnel.Name}。");
                await service.SetEnabledAsync(tunnel.Id, true);
            }
            else log?.Invoke($"[樱花] 复用官方客户端已在运行的通道：{tunnel.Name}；关服时保留。");
        }
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (true)
        {
            var current = await service.ReadTunnelsAsync(ids);
            if (current.All(t => t.Enabled && t.State == 2))
            {
                log?.Invoke("[樱花] 官方客户端已确认所有所选通道开关打开且运行中。");
                return;
            }
            if (DateTimeOffset.UtcNow >= deadline) throw new InvalidOperationException("官方客户端未能启动全部通道，请查看樱花客户端的通道日志。");
            await Task.Delay(500);
        }
    }

    public async Task StopAsync()
    {
        var failures = new List<Exception>();
        foreach (var id in enabledByUs.ToArray())
        {
            try
            {
                await service.SetEnabledAsync(id, false);
                enabledByUs.Remove(id);
                log?.Invoke($"[樱花] 已关闭本次由工具打开的官方客户端通道 {id}。");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            { failures.Add(ex); }
        }
        if (failures.Count > 0) throw new InvalidOperationException("部分官方客户端通道未能关闭，请重试保存关服或在客户端检查。", failures[0]);
    }
}
