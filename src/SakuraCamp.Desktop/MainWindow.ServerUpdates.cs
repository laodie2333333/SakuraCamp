using System.IO;
using System.Net.Http;
using System.Windows;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? serverUpdateCancellation;
    private async void CheckServerVersion_Click(object sender, RoutedEventArgs e) => await Guard(() => EnsureServerVersionAsync(false, checkOnly: true));
    private async void RepairServer_Click(object sender, RoutedEventArgs e) => await Guard(() => EnsureServerVersionAsync(true));
    private void CancelServerUpdate_Click(object sender, RoutedEventArgs e)
    {
        serverUpdateCancellation?.Cancel();
        CancelServerUpdateButton.IsEnabled = false;
        StatusText.Text = "正在停止版本检查或更新，本次不会继续开服…";
    }
    private async Task EnsureServerVersionAsync(bool repair, bool checkOnly = false)
    {
        RequireIdle();
        var world = RequireWorld();
        var executable = ServerPath.Text.Trim();
        session.SetSecrets(WorldFiles.Password(world), File.Exists(Path.Combine(world.Path, "cluster_token.txt"))
            ? File.ReadAllText(Path.Combine(world.Path, "cluster_token.txt")).Trim() : "", SakuraToken.Password.Trim());
        var secrets = new[] { WorldFiles.Password(world), SakuraToken.Password.Trim(), KleiToken.Password };
        using var cancellation = new CancellationTokenSource();
        serverUpdateCancellation = cancellation;
        CancelServerUpdateButton.IsEnabled = true;
        CancelServerUpdateButton.Visibility = Visibility.Visible;
        ServerVersionHint.Text = StatusText.Text = "正在核对世界、服务端和 Steam 正式版版本…";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        try
        {
            var runtime = SteamRuntimePaths.Resolve(store.Root);
            void Progress(string line)
            {
                var safe = LogReader.Redact(line, secrets);
                QueueSessionLog(safe);
                if (!line.StartsWith("[SteamCMD]", StringComparison.Ordinal) || line.Contains("progress:", StringComparison.OrdinalIgnoreCase))
                    Dispatcher.BeginInvoke(() => ServerVersionHint.Text = StatusText.Text = safe);
            }
            var updater = new ServerUpdateService(new SteamServerUpdater(http, Path.Combine(runtime, "steamcmd"), Progress));
            var result = await Task.Run(() => checkOnly
                ? updater.CheckAsync(world, executable, Discovery.GameExecutables(), Progress, cancellation.Token)
                : updater.EnsureAsync(world, executable, Discovery.GameExecutables(), Path.Combine(runtime, "dst-server"), repair, Progress, cancellation.Token));
            if (!checkOnly)
            {
                ServerPath.Text = result.Server.Executable;
                Persist();
                if (!result.Server.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase)) await RefreshModManagerAsync(world);
            }
            ServerVersionHint.Text = result.Summary + " · 已通过联网核验";
            StatusText.Text = "服务端版本检查通过，可继续开服。";
        }
        catch (OperationCanceledException)
        {
            ServerVersionHint.Text = "版本检查或更新已取消，未启动世界。";
            throw new InvalidOperationException("服务端检查或更新已取消，本次未开服。下次可继续更新并校验。");
        }
        catch
        {
            ServerVersionHint.Text = checkOnly ? "版本检查未通过，未更新服务端文件。详细原因见日志。" : "版本检查未通过，已阻止本次开服。详细原因见日志。";
            throw;
        }
        finally
        {
            serverUpdateCancellation = null;
            CancelServerUpdateButton.Visibility = Visibility.Collapsed;
        }
    }
}
