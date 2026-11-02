using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SakuraCamp.Core;

public interface IServerUpdateBackend
{
    Task<bool> IsCurrentAsync(ServerInstallation installation, CancellationToken cancellationToken);
    Task UpdateAsync(string directory, Action<string> progress, CancellationToken cancellationToken);
}

public sealed class ServerUpdateService(IServerUpdateBackend backend)
{
    public async Task<ServerVersionCheck> CheckAsync(WorldInfo world, string executable, IEnumerable<string> clients,
        Action<string> progress, CancellationToken cancellationToken = default)
    {
        WorldFiles.RequireStopped();
        var check = ServerVersions.Inspect(world, executable, clients);
        progress("[版本检查] " + check.Summary);
        ServerVersions.RequireCompatible(check);
        if (check.Server.SteamBuild is null || !await backend.IsCurrentAsync(check.Server, cancellationToken))
            throw new InvalidOperationException("服务端需要更新或无法确认是最新正式版。请点击“更新并校验服务端”；检查版本不会自动更新服务端文件。");
        cancellationToken.ThrowIfCancellationRequested();
        progress("[版本检查] 检查通过，未更新服务端文件。");
        return check;
    }

    public async Task<ServerVersionCheck> EnsureAsync(WorldInfo world, string executable, IEnumerable<string> clients,
        string managedDirectory, bool repair, Action<string> progress, CancellationToken cancellationToken = default)
    {
        WorldFiles.RequireStopped();
        var clientPaths = clients.ToArray();
        var check = ServerVersions.Inspect(world, executable, clientPaths);
        progress("[版本检查] " + check.Summary);
        ServerVersions.RequireReadable(check);
        var current = !repair && check.Server.Complete && check.Server.SteamBuild.HasValue && check.Server.Version.HasValue &&
            await backend.IsCurrentAsync(check.Server, cancellationToken);
        var tooOld = check.Server.Version is null || check.Server.Version < check.MinimumVersion ||
            check.Client?.Version is { } client && check.Server.Version < client;
        if (!current || tooOld || repair)
        {
            // Always update an isolated server. Never run app_update 343050 over a 322330 game directory
            // or overwrite third-party deployments (including their Mods and scripts).
            progress("[版本检查] " + (repair ? "正在更新并校验独立服务端文件…" : "服务端需要更新或安装来源无法确认，正在准备独立服务端…"));
            WorldFiles.RequireStopped();
            await backend.UpdateAsync(managedDirectory, progress, cancellationToken);
            var updated = Path.Combine(managedDirectory, "bin64", ServerVersions.ExecutableName);
            check = ServerVersions.Inspect(world, updated, clientPaths);
            progress("[版本检查] 更新后：" + check.Summary);
            // A zero process exit code alone is insufficient: verify actual files and Steam build.
            ServerVersions.RequireCompatible(check);
            if (check.Server.SteamBuild is null || !await backend.IsCurrentAsync(check.Server, cancellationToken))
                throw new InvalidOperationException("更新后仍未通过 Steam 最新版本核验，已阻止开服。请稍后重试“更新并校验服务端”。");
        }
        ServerVersions.RequireCompatible(check);
        cancellationToken.ThrowIfCancellationRequested();
        WorldFiles.RequireStopped();
        progress("[版本检查] 通过：服务端为 Steam 正式版当前版本，且不低于地上、洞穴的生成版本" +
            (check.Client is null ? "。未发现本机游戏，朋友的客户端版本仍由游戏连接时核验。" : "，与本机游戏版本一致。"));
        return check;
    }
}

public sealed class SteamServerUpdater(HttpClient http, string toolsDirectory, Action<string>? diagnostics = null) : IServerUpdateBackend
{
    public async Task<bool> IsCurrentAsync(ServerInstallation installation, CancellationToken cancellationToken)
    {
        if (installation.SteamBuild is null || installation.Branch != "public") return false;
        // Valve's listing API returns up_to_date=false even for a freshly validated Dedicated
        // Server tool. Its public depot branch build, queried through SteamCMD, is authoritative.
        if (installation.AppId == "343050")
        {
            var latest = await LatestDedicatedBuildAsync(cancellationToken);
            diagnostics?.Invoke($"[版本检查] Steam 服务端构建：本地 {installation.SteamBuild}，正式版最新 {latest}。");
            return latest == installation.SteamBuild;
        }
        try
        {
            using var document = JsonDocument.Parse(await http.GetStringAsync(
                $"https://api.steampowered.com/ISteamApps/UpToDateCheck/v1/?appid={installation.AppId}&version={installation.SteamBuild}", cancellationToken));
            var response = document.RootElement.GetProperty("response");
            if (!response.GetProperty("success").GetBoolean()) throw new InvalidOperationException("Steam 未返回有效的版本核验结果。");
            return response.GetProperty("up_to_date").GetBoolean();
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("无法连接 Steam 核验服务端最新版本，已阻止开服。请检查网络后重试；不会在版本未知时直接加载世界。", error);
        }
    }

    public static long ParseDedicatedBuild(string text)
    {
        if (text.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Steam 服务端版本信息过大。");
        var match = Regex.Match(text, "\"branches\"\\s*\\{\\s*\"public\"\\s*\\{\\s*\"buildid\"\\s*\"([0-9]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!text.Contains("\"343050\"", StringComparison.Ordinal) || !match.Success || !long.TryParse(match.Groups[1].Value, out var build) || build <= 0)
            throw new InvalidOperationException("Steam 未返回独立服务端正式版构建信息，已阻止开服。请检查网络后重试。");
        return build;
    }

    private async Task<long> LatestDedicatedBuildAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(toolsDirectory);
        using var updateLock = AcquireLock();
        diagnostics?.Invoke("[版本检查] 正在通过 SteamCMD 读取独立服务端正式版构建信息…");
        var executable = await EnsureSteamCmdAsync(cancellationToken);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = WorldFiles.HiddenProcess(executable);
            foreach (var argument in new[] { "+login", "anonymous", "+app_info_update", "1", "+app_info_print", "343050", "+quit" }) info.ArgumentList.Add(argument);
            // First launch may download the SteamCMD runtime before querying app metadata.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromMinutes(10));
            using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 SteamCMD 检查版本。");
            var output = new StringBuilder(); var outputLock = new object();
            async Task ReadAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    diagnostics?.Invoke("[SteamCMD 版本查询] " + line);
                    lock (outputLock) { if (output.Length < 2 * 1024 * 1024) output.AppendLine(line); }
                }
            }
            var readers = Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError));
            try { await process.WaitForExitAsync(deadline.Token); await readers.WaitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await process.WaitForExitAsync(); await readers;
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Steam 服务端版本查询超时，已阻止开服。请检查网络后重试。");
            }
            var text = output.ToString();
            if (attempt == 0 && process.ExitCode == 0 && text.Contains("Update complete, launching", StringComparison.OrdinalIgnoreCase) && !text.Contains("\"branches\"", StringComparison.Ordinal)) continue;
            if (process.ExitCode != 0) throw new InvalidOperationException($"Steam 版本查询失败（代码 {process.ExitCode}），已阻止开服。请检查日志和网络后重试。");
            return ParseDedicatedBuild(text);
        }
        throw new InvalidOperationException("SteamCMD 自身更新后仍未返回服务端版本，请重试。");
    }

    public async Task UpdateAsync(string directory, Action<string> progress, CancellationToken cancellationToken)
    {
        if (!SteamRuntimePaths.IsAscii(Path.GetFullPath(toolsDirectory)) || !SteamRuntimePaths.IsAscii(Path.GetFullPath(directory)))
            throw new InvalidOperationException("SteamCMD 及服务端更新目录必须使用英文路径。原目录和存档未修改。");
        WorldFiles.RequireStopped();
        var games = Process.GetProcessesByName("dontstarve_steam_x64").Concat(Process.GetProcessesByName("dontstarve_steam")).ToArray();
        foreach (var game in games) game.Dispose();
        if (games.Length > 0)
            throw new InvalidOperationException("请先退出饥荒游戏，再更新服务端，避免游戏正在保存或切换世界。");
        Directory.CreateDirectory(toolsDirectory);
        // Exclusive lock covers bootstrap, self-update and the app update, including other app instances.
        using var updateLock = AcquireLock();
        var steamcmd = await EnsureSteamCmdAsync(cancellationToken);
        Directory.CreateDirectory(directory);
        var info = WorldFiles.HiddenProcess(steamcmd);
        foreach (var argument in new[] { "+force_install_dir", Path.GetFullPath(directory), "+login", "anonymous", "+app_update", "343050", "validate", "+quit" }) info.ArgumentList.Add(argument);
        progress("[服务端更新] 通过 Valve 官方 SteamCMD 下载、更新并校验正式版；首次下载可能需要数 GB。原服务端和世界存档保持原样。");
        await RunAsync(info, progress, TimeSpan.FromMinutes(30), cancellationToken);
    }

    private FileStream AcquireLock()
    {
        try { return new FileStream(Path.Combine(toolsDirectory, "server-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new InvalidOperationException("已有服务端更新任务正在进行，请等待完成后重试。", error); }
    }

    public static (string File, long Size, string Sha256) ReadBootstrapPackage(string manifest)
    {
        if (manifest.Length > 256 * 1024) throw new InvalidOperationException("Valve 更新清单大小异常。");
        var block = Regex.Match(manifest, "\"steamcmd_win32\"\\s*\\{([^{}]+)\\}", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        string Field(string key) => Regex.Match(block.Groups[1].Value, "\"" + key + "\"\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Groups[1].Value;
        var file = Field("file"); var hash = Field("sha2");
        if (!Regex.IsMatch(file, @"^steamcmd_win32\.zip\.[a-fA-F0-9]{40}$") ||
            !Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$") || !long.TryParse(Field("size"), out var size) || size is < 100000 or > 32 * 1024 * 1024)
            throw new InvalidOperationException("无法识别 Valve 官方 SteamCMD 更新清单，已停止下载。");
        return (file, size, hash);
    }

    private async Task<string> EnsureSteamCmdAsync(CancellationToken cancellationToken)
    {
        var executable = Path.Combine(toolsDirectory, "steamcmd.exe");
        if (File.Exists(executable)) return executable;
        // Valve's old installer ZIP contains a 2013 HTTP-only bootstrapper. Fetch the current
        // bootstrap package from Valve's HTTPS manifest instead and verify its published SHA-256.
        const string cdn = "https://client-update.akamai.steamstatic.com/";
        var package = ReadBootstrapPackage(await http.GetStringAsync(cdn + "steam_cmd_win32", cancellationToken));
        using var response = await http.GetAsync(cdn + package.File, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != package.Size) throw new InvalidOperationException("SteamCMD 下载大小与官方清单不符。");
        using var archiveBytes = new MemoryStream();
        await using (var download = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[65536]; int count;
            while ((count = await download.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (archiveBytes.Length + count > 32 * 1024 * 1024) throw new InvalidOperationException("SteamCMD 下载大小异常。");
                archiveBytes.Write(buffer, 0, count);
            }
        }
        if (archiveBytes.Length != package.Size || !Convert.ToHexString(SHA256.HashData(archiveBytes.GetBuffer().AsSpan(0, (int)archiveBytes.Length))).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SteamCMD 下载与 Valve 官方大小或 SHA-256 校验不符，未安装或运行。");
        archiveBytes.Position = 0;
        using var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Read);
        var entry = archive.GetEntry("steamcmd.exe") ?? throw new InvalidOperationException("Valve 下载包中没有 SteamCMD。");
        if (entry.Length is < 100000 or > 32 * 1024 * 1024) throw new InvalidOperationException("SteamCMD 程序大小异常。");
        var stage = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = entry.Open())
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write)) await input.CopyToAsync(output, cancellationToken);
            File.Move(stage, executable, true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
        return executable;
    }

    public static async Task RunAsync(ProcessStartInfo info, Action<string> progress, TimeSpan timeout, CancellationToken cancellationToken)
        => await RunAttemptAsync(info, progress, timeout, cancellationToken, true);

    private static async Task RunAttemptAsync(ProcessStartInfo info, Action<string> progress, TimeSpan timeout, CancellationToken cancellationToken, bool retryBootstrap)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("无法启动 SteamCMD。");
        var success = 0; var error = 0; var bootstrapped = 0;
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                progress("[SteamCMD] " + line);
                if (line.Contains("Success! App '343050' fully installed.", StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref success, 1);
                if (line.Contains("Update complete, launching", StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref bootstrapped, 1);
                if (line.Contains("ERROR!", StringComparison.OrdinalIgnoreCase) || line.Contains("FAILED", StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref error, 1);
            }
        }
        var outputs = Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await outputs.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
            await outputs;
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("服务端更新已取消；下次检查时会继续下载并校验，不会启动世界。", cancellationToken);
            throw new InvalidOperationException("服务端更新超过 30 分钟，已停止本次更新并阻止开服。请检查网络后重试，已下载文件会保留。");
        }
        if (retryBootstrap && process.ExitCode == 0 && bootstrapped == 1 && success == 0 && error == 0)
        {
            progress("[服务端更新] SteamCMD 自身更新完成，正在继续服务端更新…");
            await RunAttemptAsync(info, progress, timeout, cancellationToken, false);
            return;
        }
        if (process.ExitCode != 0 || success == 0 || error != 0)
            throw new InvalidOperationException($"SteamCMD 未成功完成服务端更新（退出代码 {process.ExitCode}），已阻止开服。请在日志中查看下载或磁盘错误，再重试更新。");
    }
}
