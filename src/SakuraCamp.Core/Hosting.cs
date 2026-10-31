using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace SakuraCamp.Core;

public static class WorldFiles
{
    public static string Backup(string source, string backupRoot)
    {
        var fullSource = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(backupRoot, Path.GetFileName(source) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
        if (target.StartsWith(fullSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("备份目录不能放在世界存档内部。");
        CopyFolder(fullSource, target);
        return target;
    }
    private static void CopyFolder(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("存档含目录链接，无法安全备份：" + source);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("存档含文件链接，无法安全备份。");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.GetDirectories(source)) CopyFolder(folder, Path.Combine(target, Path.GetFileName(folder)));
    }
    public static void RequireStopped()
    {
        var processes = Process.GetProcessesByName("dontstarve_dedicated_server_nullrenderer_x64");
        try
        {
            if (processes.Length > 0) throw new InvalidOperationException("检测到已有饥荒服务器。请先保存并关闭游戏内的世界或旧服务器，再应用设置。工具不会接管或结束其他程序启动的世界。");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public static string Prepare(LaunchPlan plan, string backupRoot)
    {
        plan.Validate();
        RequireStopped();
        var bound = IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners();
        if (plan.Shards.Any(s => bound.Any(p => p.Port == s.Port))) throw new InvalidOperationException("游戏端口已被占用，请先关闭其他服务器或选择新的隧道。");
        // Validate every edit before writing any file. Always make a complete backup first.
        var edits = plan.Shards.Select(s =>
        {
            var path = Path.Combine(plan.World.Path, s.Name, "server.ini");
            var ini = IniDocument.Load(path); ini.Set("NETWORK", "server_port", s.Port.ToString());
            return (path, ini);
        }).ToArray();
        var backup = Backup(plan.World.Path, backupRoot);
        foreach (var (path, ini) in edits) ini.Save(path);
        return backup;
    }
    public static ProcessStartInfo ServerStart(LaunchPlan plan, string shard)
    {
        var parent = Directory.GetParent(plan.World.Path) ?? throw new InvalidOperationException("无效存档路径。");
        var accountLayout = Regex.IsMatch(parent.Name, @"^\d+$");
        var configDir = accountLayout ? parent.Parent! : parent;
        var info = HiddenProcess(plan.ServerExe);
        var args = new List<string> { "-persistent_storage_root", StorageRoot(configDir.Parent!.FullName), "-conf_dir", configDir.Name, "-cluster", plan.World.Folder };
        if (accountLayout) args.AddRange(["-ownerdir", parent.Name, "-clouddir", parent.Name]);
        if (Directory.Exists(plan.WorkshopPath)) args.AddRange(["-ugc_directory", plan.WorkshopPath]);
        args.AddRange(["-console", "-shard", shard]);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public static string StorageRoot(string path)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.Combine(documents, "Klei"), StringComparison.OrdinalIgnoreCase)) return "APP:Klei/";
        if (Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).Equals(documents, StringComparison.OrdinalIgnoreCase)) return "APP:";
        return path;
    }
    public static ProcessStartInfo HiddenProcess(string exe) => new(exe)
    {
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };
    public static string Password(WorldInfo world) => IniDocument.Load(Path.Combine(world.Path, "cluster.ini")).Get("NETWORK", "cluster_password");
    public static string Connect(string host, int port, string password) => $"c_connect({Lua(host)}, {port}" + (password.Length > 0 ? $", {Lua(password)})" : ")");
    public static string Lua(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
}

public static class LogReader
{
    public static string Tail(string path, int maxBytes = 65536)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maxBytes) file.Seek(-maxBytes, SeekOrigin.End);
        using var reader = new StreamReader(file, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
    public static string Redact(string text, params string[] secrets)
    {
        foreach (var secret in secrets.Where(s => s.Length > 0).OrderByDescending(s => s.Length)) text = text.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
        text = Regex.Replace(text, @"(?:pds-|pcl-)[^\s\""']+", "[令牌已隐藏]");
        text = Regex.Replace(text, @"(?i)((?:cluster_password|cluster_key|token|password)\s*[=:]\s*)[^\r\n]+", "$1[已隐藏]");
        return text;
    }
    public static IReadOnlyList<string> Diagnose(string text)
    {
        var results = new List<string>();
        if (text.Contains("MODS_DOWNLOAD", StringComparison.OrdinalIgnoreCase) || text.Contains("download mods failed", StringComparison.OrdinalIgnoreCase) || text.Contains("E_MODS_DOWNLOAD", StringComparison.OrdinalIgnoreCase))
            results.Add("客户端下载服务器 Mod 失败。退出游戏，等待 Steam 下载完成后重试；需要客户端 client_log.txt 才能定位具体项目。");
        if (text.Contains("LUA ERROR", StringComparison.OrdinalIgnoreCase)) results.Add("检测到 Lua 错误，请查看错误附近的 workshop-编号，核对该 Mod 与当前游戏版本。");
        if (Regex.IsMatch(text, @"(?i)(invalid|expired|failed)[^\r\n]{0,40}token|token[^\r\n]{0,40}(invalid|expired)")) results.Add("服务器令牌可能无效或已过期，请在 Klei 网站重新生成。");
        if (Regex.IsMatch(text, @"(?i)bind[^\r\n]{0,30}(fail|error)|address already in use")) results.Add("端口绑定失败，可能有其他世界或程序占用了端口。");
        var ids = Regex.Matches(text, @"workshop-(\d+)").Select(m => m.Groups[1].Value).Distinct().ToArray();
        if (results.Count == 0) results.Add("未识别到明确错误。检查最近一次连接附近的日志；仅看到 Mod 编号不能判断该 Mod 有问题。");
        if (ids.Length > 0) results.Add("日志出现的 Mod 编号：" + string.Join("、", ids));
        return results;
    }
}

public sealed class ManagedSession
{
    private sealed record Child(string Name, Process Process, TaskCompletionSource Ready, int RequiredSignals)
    {
        public int Signals;
        public ConcurrentQueue<string> Recent { get; } = new();
        public HashSet<string> ReadyProxies { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim InputLock { get; } = new(1, 1);
        public string? StatusRequestId;
        public TaskCompletionSource<ServerSample>? StatusReply;
        public TimeSpan LastCpu;
        public DateTimeOffset LastCpuAt;
    }
    private readonly List<Child> children = [];
    private readonly ConcurrentDictionary<string, Task> readers = new();
    private string[] secrets = [];
    public event Action<string>? Log;
    public event Action<string, string>? State;
    public bool HasProcesses => externalTunnelsActive || children.Any(c => !c.Process.HasExited);
    private bool externalTunnelsActive;
    private Func<Task>? stopExternalTunnels;
    public WorldInfo? World { get; private set; }
    private bool stopping;
    private DateTimeOffset startedAt;
    private readonly SemaphoreSlim statusLock = new(1, 1);
    public bool IsReady { get; private set; }
    private TaskCompletionSource<Exception> startupFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void SetSecrets(params string[] values) => secrets = values;
    public void Emit(string text) => Log?.Invoke(LogReader.Redact(text, secrets));

    // Product entry point: missing tunnel startup can never silently become a world-only launch.
    public Task StartWithTunnelsAsync(LaunchPlan plan, string backupRoot, ProcessStartInfo frpc,
        Func<string, ProcessStartInfo>? factory = null, TimeSpan? timeout = null, Func<Task>? verifyOnline = null)
    {
        ArgumentNullException.ThrowIfNull(frpc);
        return StartAsync(plan, backupRoot, frpc, factory, timeout, verifyOnline);
    }

    public Task StartWithLauncherAsync(LaunchPlan plan, string backupRoot, Func<Task> startTunnels, Func<Task> stopTunnels,
        Func<Task> verifyOnline, Func<string, ProcessStartInfo>? factory = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(startTunnels); ArgumentNullException.ThrowIfNull(stopTunnels); ArgumentNullException.ThrowIfNull(verifyOnline);
        return StartAsync(plan, backupRoot, factory: factory, timeout: timeout, verifyOnline: verifyOnline,
            startExternal: startTunnels, stopExternal: stopTunnels);
    }

    public async Task StartAsync(LaunchPlan plan, string backupRoot, ProcessStartInfo? frpc = null, Func<string, ProcessStartInfo>? factory = null, TimeSpan? timeout = null, Func<Task>? verifyOnline = null,
        Func<Task>? startExternal = null, Func<Task>? stopExternal = null)
    {
        if (HasProcesses) throw new InvalidOperationException("当前已有工具管理的服务器运行。");
        foreach (var old in children) old.Process.Dispose();
        children.Clear(); readers.Clear(); stopping = false; IsReady = false;
        startupFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Emit("正在检查端口并完整备份世界…");
        var backup = await Task.Run(() => WorldFiles.Prepare(plan, backupRoot));
        Emit("备份完成：" + backup);
        World = plan.World;
        try
        {
            if (startExternal is not null)
            {
                stopExternalTunnels = stopExternal; externalTunnelsActive = true;
                State?.Invoke("樱花", "正在打开客户端通道");
                await startExternal();
                if (verifyOnline is not null) await verifyOnline();
                State?.Invoke("樱花", "全部在线");
            }
            if (frpc is not null)
            {
                Launch("樱花", frpc, plan.Shards.Count());
                var tunnel = children.Single(c => c.Name == "樱花");
                var winner = await Task.WhenAny(tunnel.Ready.Task, startupFailure.Task).WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
                if (winner == startupFailure.Task) throw await startupFailure.Task;
                await tunnel.Ready.Task;
                if (verifyOnline is not null) await verifyOnline();
            }
            foreach (var (shard, _) in plan.Shards) Launch(shard, factory?.Invoke(shard) ?? WorldFiles.ServerStart(plan, shard));
            var required = Task.WhenAll(children.Select(c => c.Ready.Task));
            var completed = await Task.WhenAny(required, startupFailure.Task).WaitAsync(timeout ?? TimeSpan.FromMinutes(3));
            if (completed == startupFailure.Task) throw await startupFailure.Task;
            await required;
            if (!HasProcesses || children.Any(c => c.Process.HasExited)) throw new InvalidOperationException("启动过程中有进程退出，请查看日志。");
            startedAt = DateTimeOffset.UtcNow; IsReady = true;
            Emit("地上与洞穴（如有）的启动信号已确认。外网加入和洞穴切换仍需朋友实际测试。");
        }
        catch
        {
            Emit("启动未完成，正在请求已启动的服务器保存并退出…");
            await StopAsync();
            throw;
        }
    }
    private void Launch(string name, ProcessStartInfo info, int requiredSignals = 1)
    {
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!process.Start()) throw new InvalidOperationException($"无法启动 {name}。");
        var child = new Child(name, process, ready, requiredSignals); children.Add(child);
        if (name == "樱花") Emit($"[樱花] 独立版 frpc 已启动，进程 ID {process.Id}；输出记录在本工具，不写入官方启动器日志。");
        State?.Invoke(name, "启动中");
        // Output can arrive continuously during world/Mod loading. Never run its
        // parsing loop on the caller's UI synchronization context.
        readers[name] = Task.Run(() => Pump(child));
        _ = Watch(child);
    }
    private async Task Pump(Child child)
    {
        async Task Read(StreamReader stream)
        {
            while (await stream.ReadLineAsync() is { } line)
            {
                // Keep telemetry (including player names) out of exported diagnostics and the visible log.
                if (child.Name != "樱花" && line.Contains("[SakuraCamp.Status:", StringComparison.Ordinal))
                {
                    var requestId = child.StatusRequestId;
                    if (requestId is not null && ServerTelemetry.TryParse(line, requestId, child.Name, out var sample)) child.StatusReply?.TrySetResult(sample!);
                    continue;
                }
                Emit($"[{child.Name}] {line}");
                child.Recent.Enqueue(LogReader.Redact(line, secrets));
                while (child.Recent.Count > 12) child.Recent.TryDequeue(out _);
                var success = child.Name == "樱花"
                    ? Regex.IsMatch(line, @"(?i)start proxy success|隧道.*启动成功|启动.*隧道.*成功")
                    : child.Name == "Master" ? line.Contains("Server registered", StringComparison.OrdinalIgnoreCase)
                    : Regex.IsMatch(line, @"(?i)secondary shard (LUA )?is now ready");
                if (success && child.Name == "樱花")
                {
                    // Standard frpc output prefixes proxy messages with [proxy-name]. Reconnects for
                    // one proxy must never be counted as readiness for the other shard.
                    var proxyName = FrpcInstaller.ReadyProxyName(line);
                    lock (child.ReadyProxies) success = proxyName is not null && child.ReadyProxies.Add(proxyName);
                }
                if (success && Interlocked.Increment(ref child.Signals) >= child.RequiredSignals && child.Ready.TrySetResult()) State?.Invoke(child.Name, "已就绪");
            }
        }
        try { await Task.WhenAll(Read(child.Process.StandardOutput), Read(child.Process.StandardError)); }
        catch (IOException) { }
    }
    private async Task Watch(Child child)
    {
        await child.Process.WaitForExitAsync();
        if (readers.TryGetValue(child.Name, out var output)) await output;
        var detail = string.Join(Environment.NewLine, child.Recent.Where(s => !string.IsNullOrWhiteSpace(s)).TakeLast(5));
        var exception = new InvalidOperationException($"{child.Name} 已退出（代码 {child.Process.ExitCode}）。\n{detail}");
        if (!stopping) startupFailure.TrySetResult(exception);
        IsReady = false;
        child.StatusReply?.TrySetException(exception);
        child.Ready.TrySetException(exception);
        State?.Invoke(child.Name, "已停止");
        Emit($"{child.Name} 已退出（代码 {child.Process.ExitCode}）。");
        if (!stopping) Emit("有进程意外退出。请保存并关闭其余服务器，检查日志后重新启动。");
    }
    public async Task<bool> StopAsync(TimeSpan? timeout = null)
    {
        stopping = true; IsReady = false;
        var servers = children.Where(c => c.Name != "樱花" && !c.Process.HasExited).ToArray();
        foreach (var child in servers)
        {
            State?.Invoke(child.Name, "保存退出中");
            try { await WriteInputAsync(child, "c_shutdown(true)"); }
            catch (IOException) { }
        }
        try { await Task.WhenAll(servers.Select(c => c.Process.WaitForExitAsync())).WaitAsync(timeout ?? TimeSpan.FromSeconds(40)); }
        catch (TimeoutException) { Emit("服务器尚未退出。请继续等待并再次点击保存关服；为保护存档，没有强制结束进程。"); return false; }
        foreach (var child in children.Where(c => c.Name == "樱花" && !c.Process.HasExited))
        {
            child.Process.Kill(true); await child.Process.WaitForExitAsync(); // Only our own frpc after all worlds saved.
        }
        if (externalTunnelsActive && stopExternalTunnels is not null)
        {
            try { await stopExternalTunnels(); externalTunnelsActive = false; stopExternalTunnels = null; State?.Invoke("樱花", "已停止"); }
            catch (Exception ex) { Emit("关闭官方客户端通道失败：" + ex.Message); return false; }
        }
        await Task.WhenAll(readers.Values);
        Emit("服务器已保存退出，工具启动的穿透进程已关闭。");
        return true;
    }

    private static async Task WriteInputAsync(Child child, string command)
    {
        await child.InputLock.WaitAsync();
        try { await child.Process.StandardInput.WriteLineAsync(command); await child.Process.StandardInput.FlushAsync(); }
        finally { child.InputLock.Release(); }
    }

    public async Task<SessionStatus> ReadStatusAsync(TimeSpan? timeout = null)
    {
        await statusLock.WaitAsync();
        try
        {
            if (!IsReady || stopping) return new(DateTimeOffset.UtcNow, TimeSpan.Zero, []);
            var shards = children.Where(c => c.Name != "樱花").ToArray();
            var results = await Task.WhenAll(shards.Select(async child =>
            {
                if (child.Process.HasExited) return new ShardStatus(child.Name, false, null, "进程已退出", null, null);
                double? cpu = null; long? memory = null;
                try
                {
                    child.Process.Refresh(); memory = child.Process.WorkingSet64;
                    var now = DateTimeOffset.UtcNow; var total = child.Process.TotalProcessorTime;
                    if (child.LastCpuAt != default) cpu = Math.Clamp((total - child.LastCpu).TotalMilliseconds / (now - child.LastCpuAt).TotalMilliseconds / Environment.ProcessorCount * 100, 0, 100);
                    child.LastCpu = total; child.LastCpuAt = now;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                var id = Guid.NewGuid().ToString("N");
                var reply = new TaskCompletionSource<ServerSample>(TaskCreationOptions.RunContinuationsAsynchronously);
                child.StatusReply = reply; child.StatusRequestId = id;
                try
                {
                    if (stopping) return new ShardStatus(child.Name, !child.Process.HasExited, null, "正在保存关服", cpu, memory);
                    await WriteInputAsync(child, ServerTelemetry.Command(id));
                    var sample = await reply.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(3));
                    return new ShardStatus(child.Name, !child.Process.HasExited, sample, "实时数据已更新", cpu, memory);
                }
                catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException)
                { return new ShardStatus(child.Name, !child.Process.HasExited, null, "暂未收到状态，等待下次刷新", cpu, memory); }
                finally { child.StatusRequestId = null; child.StatusReply = null; }
            }));
            return new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - startedAt, results);
        }
        finally { statusLock.Release(); }
    }
}
