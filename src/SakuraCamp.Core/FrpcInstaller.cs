using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SakuraCamp.Core;

public static class FrpcInstaller
{
    public static string? ReadyProxyName(string line)
    {
        // Match the bracket immediately before the success message. Earlier brackets contain
        // the node/run ID shared by every tunnel; generic "UDP 隧道启动成功" has no identity.
        var match = Regex.Match(line, @"\[([^\[\]]+)\]\s*(?:start proxy success|隧道\s*启动成功)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static bool IsLauncherCompanion(string exe) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, "SakuraFrpService.exe"));

    public static async Task ProbeAsync(string exe)
    {
        if (!File.Exists(exe)) throw new InvalidOperationException("找不到独立版 frpc，请下载独立版或使用手动模式。");
        if (IsLauncherCompanion(exe)) throw new InvalidOperationException("这个 frpc 是樱花启动器专用版，不能由其他工具直接启动。请下载官方独立版 frpc，或使用手动模式。");
        var start = WorldFiles.HiddenProcess(exe); start.ArgumentList.Add("-h");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法检查 frpc。");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { if (!process.HasExited) process.Kill(true); throw new InvalidOperationException("frpc 检查超时，请选择官方独立版。"); }
        var output = await stdout + await stderr;
        if (process.ExitCode != 0 || !output.Contains("fetch_config", StringComparison.Ordinal))
            throw new InvalidOperationException("所选 frpc 不支持独立启动，请选择官方独立版。");
    }

    public static async Task<string> InstallAsync(HttpClient client, string directory)
    {
        using var metadata = JsonDocument.Parse(await client.GetStringAsync("https://api.natfrp.com/v4/system/clients?download=true"));
        var release = metadata.RootElement.GetProperty("frpc");
        var asset = release.GetProperty("archs").GetProperty("windows_amd64");
        var uri = new Uri(asset.GetProperty("url").GetString()!);
        if (uri.Scheme != "https" || uri.Host != "nya.globalslb.net") throw new InvalidOperationException("官方下载地址发生变化，已停止下载，请前往樱花官网获取独立版。");
        var hash = asset.GetProperty("hash").GetString()!;
        var size = asset.GetProperty("size").GetInt64();
        if (size is < 100000 or > 100000000) throw new InvalidOperationException("官方客户端下载大小异常。");
        var bytes = await client.GetByteArrayAsync(uri);
        if (bytes.LongLength != size || !Convert.ToHexString(MD5.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("独立版 frpc 与官方公布的大小或校验值不一致，请重试。");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "frpc.exe");
        // Validate a downloaded file before replacing a previously usable client.
        var stage = Path.Combine(directory, "download-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            await File.WriteAllBytesAsync(stage, bytes);
            await ProbeAsync(stage);
            File.Move(stage, destination, true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
        return destination;
    }
}
