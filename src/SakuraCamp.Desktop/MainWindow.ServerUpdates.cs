using System.IO;
using System.Net.Http;
using System.Windows;
using SakuraCamp.Core;
using Microsoft.Win32;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? serverUpdateCancellation;
    private async void RecoverServerPath_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        RequireIdle();
        await RecoverServerPathAsync();
        if (!File.Exists(ServerPath.Text.Trim())) throw new InvalidOperationException("未发现可用服务端，请浏览选择已有服务器程序，或在更新时选择安装目录。");
        StatusText.Text = "已找到可用服务端：" + ServerPath.Text;
    });
    private async Task RecoverServerPathAsync()
    {
        var selected = ServerPath.Text.Trim();
        if (File.Exists(selected)) { Persist(); return; }
        var history = settings.ServerHistory.ToArray();
        var recovered = await Task.Run(() => ServerLocations.Recover(selected, history, Discovery.ServerExecutables()));
        if (recovered.Length == 0 || recovered.Equals(selected, StringComparison.OrdinalIgnoreCase)) return;
        ServerPath.Text = recovered;
        Persist();
        AppendLog("[服务端路径] 原路径已失效，恢复已有程序：" + recovered);
        ServerVersionHint.Text = "待检查";
        if (CurrentWorld is { } world) await RefreshModManagerAsync(world);
    }
    private async void CheckServerVersion_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        try { await EnsureServerVersionAsync(false, checkOnly: true); }
        catch (Exception error)
        {
            AppendLog("[版本检查] 检查未通过：" + error);
            ServerVersionHint.Text = StatusText.Text = "检查未通过，请点击“更新并校验服务端”。";
        }
    });
    private async void RepairServer_Click(object sender, RoutedEventArgs e) => await Guard(() => EnsureServerVersionAsync(true));
    private void CancelServerUpdate_Click(object sender, RoutedEventArgs e)
    {
        serverUpdateCancellation?.Cancel();
        CancelServerUpdateButton.IsEnabled = false;
        StatusText.Text = "正在取消本次操作；Steam 中的任务请在 Steam 内停止。";
    }
    private async Task EnsureServerVersionAsync(bool repair, bool checkOnly = false)
    {
        RequireIdle();
        await RecoverServerPathAsync();
        var world = RequireWorld();
        var executable = ServerPath.Text.Trim();
        var installDirectory = "";
        if (!File.Exists(executable) && !checkOnly)
        {
            var picker = new OpenFolderDialog { Title = "未找到已有服务端：请选择英文空文件夹安装（约 5 GB，可取消）" };
            if (picker.ShowDialog(this) != true) throw new InvalidOperationException("未选择安装目录，本次未下载或开服。");
            ServerLocations.ValidateNewInstallDirectory(picker.FolderName, world.Path);
            installDirectory = picker.FolderName;
            executable = "";
        }
        if (!File.Exists(executable) && checkOnly) throw new InvalidOperationException("未找到服务端；已搜索历史路径和 Steam 库。请浏览选择已有程序，或点击更新并选择安装目录。");
        session.SetSecrets(WorldFiles.Password(world), File.Exists(Path.Combine(world.Path, "cluster_token.txt"))
            ? File.ReadAllText(Path.Combine(world.Path, "cluster_token.txt")).Trim() : "", SakuraToken.Password.Trim());
        var secrets = new[] { WorldFiles.Password(world), SakuraToken.Password.Trim(), KleiToken.Password };
        using var cancellation = new CancellationTokenSource();
        serverUpdateCancellation = cancellation;
        CancelServerUpdateButton.IsEnabled = true;
        CancelServerUpdateButton.Visibility = checkOnly ? Visibility.Collapsed : Visibility.Visible;
        ServerVersionHint.Text = StatusText.Text = checkOnly ? "正在检查服务端版本…" : "正在更新并校验服务端版本";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        try
        {
            var runtime = SteamRuntimePaths.Resolve(store.Root);
            void Progress(string line)
            {
                var safe = LogReader.Redact(line, secrets);
                QueueSessionLog(safe);
            }
            var updater = new ServerUpdateService(new SteamServerUpdater(http, Path.Combine(runtime, "steamcmd"), Progress));
            var result = await Task.Run(() => checkOnly
                ? updater.CheckAsync(world, executable, Discovery.GameExecutables(), Progress, cancellation.Token)
                : updater.EnsureAsync(world, executable, Discovery.GameExecutables(), installDirectory, repair, Progress, cancellation.Token));
            if (!checkOnly)
            {
                ServerPath.Text = result.Server.Executable;
                Persist();
                if (!result.Server.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase)) await RefreshModManagerAsync(world);
            }
            ServerVersionHint.Text = StatusText.Text = checkOnly
                ? "检查通过，服务端已是最新版本，可以一键开服。"
                : "更新并校验完成，服务端已是最新版本，可以一键开服。";
            AppendLog(ServerVersionHint.Text);
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
