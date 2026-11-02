using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SakuraCamp.Core;
using SakuraCamp.Desktop;

internal static class Program
{
    private static readonly List<string> passed = [];
    private static readonly BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed.Add(message); Console.WriteLine("PASS: " + message);
    }

    [STAThread]
    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "SakuraCamp-image-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new App(); app.InitializeComponent();
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        // Isolate settings and diagnostics; never read or write the user's configuration.
        typeof(App).GetProperty(nameof(App.DataRoot))!.SetValue(null, Path.Combine(root, "app-data"));
        app.Dispatcher.BeginInvoke(async () =>
        {
            try { await VerifyAsync(root); Console.WriteLine($"ALL {passed.Count} IMAGE CHECKS PASSED. Fixtures: {root}"); }
            catch (Exception error) { Console.WriteLine(error); Environment.ExitCode = 1; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        Dispatcher.Run();
    }

    private static byte[] Texture(int format)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(0x5845544bU); writer.Write((uint)(format << 4 | 1 << 13));
        writer.Write((ushort)4); writer.Write((ushort)4); writer.Write((ushort)4); writer.Write(16U);
        writer.Write(new byte[] { 255, 255, 0, 0, 0, 0, 0, 0, 0, 248, 0, 248, 0, 0, 0, 0 });
        return stream.ToArray();
    }

    private static async Task VerifyAsync(string root)
    {
        Check(ImageFailures.IsRecoverable(new InvalidDataException()), "unsupported texture and ZIP errors are recoverable");
        Check(!ImageFailures.IsRecoverable(new InvalidOperationException()), "unrelated programming errors are not swallowed");
        using (var valid = new MemoryStream(Texture(2)))
        {
            var bitmap = CharacterAvatars.ReadTexture(valid); var pixels = new byte[64]; bitmap.CopyPixels(pixels, 16, 0);
            Check(bitmap.IsFrozen && bitmap.PixelWidth == 4 && pixels[2] == 255 && pixels[3] == 255, "supported DXT5 still decodes correct opaque red pixels");
        }

        var game = Path.Combine(root, "game"); var exe = Path.Combine(game, "bin64", "server.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        var bundles = Path.Combine(game, "data", "databundles"); Directory.CreateDirectory(bundles);
        var bundle = Path.Combine(bundles, "images.zip");
        var window = new MainWindow();
        ((TextBox)window.FindName("ServerPath")).Text = exe;
        var warnings = new List<string>();
        const string atlas = "<Atlas><Texture filename='bad.tex'/><Elements><Element name='icon.tex' u1='0' u2='1' v1='0' v2='1'/></Elements></Atlas>";
        var rule = new WorldRuleDefinition("weather", "天气", "环境", 0, 0, [], false, LuaValue.Nil, [])
        { IconAtlas = "images/bad.xml", IconName = "icon.tex" };
        for (var variant = 0; variant < 3; variant++)
        {
            if (variant < 2)
            {
                using var zip = ZipFile.Open(bundle, ZipArchiveMode.Create);
                using (var entry = zip.CreateEntry("images/avatars.tex").Open()) entry.Write(variant == 0 ? Texture(0) : new byte[] { 1, 2 });
                using (var entry = new StreamWriter(zip.CreateEntry("images/avatars.xml").Open())) entry.Write(atlas);
                using (var entry = zip.CreateEntry("images/bad.tex").Open()) entry.Write(Texture(0));
                using (var entry = new StreamWriter(zip.CreateEntry("images/bad.xml").Open())) entry.Write(atlas);
            }
            else File.WriteAllText(bundle, "damaged zip");
            typeof(MainWindow).GetField("avatarExecutable", flags)!.SetValue(window, "");
            await (Task)typeof(MainWindow).GetMethod("LoadAvatarsAsync", flags)!.Invoke(window, null)!;
            Check(((System.Collections.IDictionary)typeof(MainWindow).GetField("characterAvatars", flags)!.GetValue(window)!).Count == 0,
                $"real window avatar loader survives image variant {variant} with placeholder avatars");
            var rules = WorldRuleImages.Load(exe, [rule], warnings.Add);
            Check(rules.Count == 0, $"world rule image variant {variant} falls back without preventing editing");
            File.Delete(bundle);
        }
        var folder = Path.Combine(root, "mod"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "modinfo.lua"), "name='test'");
        File.WriteAllBytes(Path.Combine(folder, "icon.tex"), Texture(0));
        var mod = new InstalledMod("workshop-123", "Test", "1", "Test", "", folder, false, true, "", "icon.tex", [], "");
        Check(ModImages.Load(mod, warnings.Add) is null, "unsupported Mod icon falls back to card text");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4)));
        using (var png = File.Create(Path.Combine(folder, "icon.png"))) encoder.Save(png);
        File.WriteAllText(Path.Combine(folder, "preview.png"), "damaged PNG");
        Check(ModImages.Load(mod, warnings.Add) is BitmapSource { IsFrozen: true }, "bad KTEX and bad preview do not hide a valid later PNG fallback");
        var row = new WorldRuleRow(rule, "Master", null); row.Editable = true;
        Check(row.Editable && row.Icon is null, "rule remains editable without its picture");
        var logs = (Queue<string>)typeof(MainWindow).GetField("logLines", flags)!.GetValue(window)!;
        Check(logs.Any(l => l.Contains("角色头像读取失败")) && warnings.Count >= 5, "image failures produce diagnostic details instead of fatal dialogs");
        typeof(MainWindow).GetMethod("AppendLog", flags)!.Invoke(window, ["image fallback test"]);
        var exported = LogReader.Redact(string.Join(Environment.NewLine, logs));
        File.WriteAllText(Path.Combine(root, "SakuraCamp-diagnostics.txt"), exported);
        Check(exported.Contains("image fallback test") && exported.Contains("角色头像读取失败"), "diagnostic buffer remains available after avatar decode failure");
        typeof(MainWindow).GetMethod("AppendLog", flags)!.Invoke(window, ["Version: desktop-start-version"]);
        await Task.Run(() =>
        {
            for (var i = 0; i < 10000; i++) typeof(MainWindow).GetMethod("QueueSessionLog", flags)!.Invoke(window, ["[Master] full-log-" + i]);
        });
        typeof(MainWindow).GetMethod("DrainPendingLogs", flags)!.Invoke(window, [int.MaxValue]);
        Check(logs.Count <= 600 && !logs.Any(l => l.Contains("desktop-start-version")), "UI buffer stays bounded while complete journal retains early startup");
        typeof(MainWindow).GetMethod("ClearLog_Click", flags)!.Invoke(window, [null, new System.Windows.RoutedEventArgs()]);
        var fullExportPath = Path.Combine(root, "complete-desktop-diagnostics.txt");
        await (Task)typeof(MainWindow).GetMethod("ExportDiagnosticsAsync", flags)!.Invoke(window, [fullExportPath])!;
        var fullExport = await File.ReadAllTextAsync(fullExportPath);
        Check(logs.Count == 0 && fullExport.Contains("Version: desktop-start-version") && fullExport.Contains("full-log-0\r\n") && fullExport.Contains("full-log-9999"),
            "actual desktop export retains version and all 10000 queued messages after clear display");
        Check(fullExport.Split("full-log-").Length - 1 == 10000, "complete journal records each queued server line once, without UI drain duplicates");
        var updateButton = window.FindName("CancelServerUpdateButton") as System.Windows.Controls.Button;
        Check(window.FindName("ServerVersionHint") is TextBlock && updateButton?.Visibility == System.Windows.Visibility.Collapsed,
            "server version UI loads with cancel hidden until a real check begins");
        // Close without showing the real startup window; dispose timers and log tasks.
        await (Task)typeof(MainWindow).GetMethod("FinishLogsAsync", flags)!.Invoke(window, null)!;
        window.Close();

        using (var zip = ZipFile.Open(bundle, ZipArchiveMode.Create))
        {
            using (var entry = zip.CreateEntry("images/avatars.tex").Open()) entry.Write(Texture(0));
            using (var entry = new StreamWriter(zip.CreateEntry("images/avatars.xml").Open())) entry.Write(atlas);
        }
        var worlds = Path.Combine(root, "empty-worlds"); Directory.CreateDirectory(worlds);
        File.WriteAllText(exe, "fixture server");
        var missingServer = Path.Combine(root, "deleted-download", "bin64", ServerVersions.ExecutableName);
        new SettingsStore(App.DataRoot).Save(new AppSettings { SaveRoot = worlds, ServerExe = missingServer, ServerHistory = [exe], WorkshopPath = root });
        var startup = new MainWindow();
        startup.Show();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var watch = (DispatcherTimer)typeof(MainWindow).GetField("modWatchTimer", flags)!.GetValue(startup)!;
        while (!watch.IsEnabled && DateTime.UtcNow < deadline) await Task.Delay(30);
        Check(startup.IsVisible && watch.IsEnabled, "actual Loaded startup completes after unsupported avatar compression without a fatal dialog");
        Check(((TextBox)startup.FindName("ServerPath")).Text == exe && new SettingsStore(App.DataRoot).Load().ServerExe == exe,
            "actual startup recovers a deleted selected server from history and persists the surviving path");
        ((TextBox)startup.FindName("ServerPath")).Text = missingServer;
        await (Task)typeof(MainWindow).GetMethod("RecoverServerPathAsync", flags)!.Invoke(startup, null)!;
        Check(((TextBox)startup.FindName("ServerPath")).Text == exe, "runtime rescan restores an existing server after selected download is removed");
        var tabs = (TabControl)startup.FindName("Tabs");
        foreach (var tab in tabs.Items.Cast<TabItem>()) tabs.SelectedItem = tab;
        Check(ReferenceEquals(tabs.SelectedItem, tabs.Items[tabs.Items.Count - 1]), "navigation still responds after image failure");
        var startupLogs = (Queue<string>)typeof(MainWindow).GetField("logLines", flags)!.GetValue(startup)!;
        Check(startupLogs.Any(l => l.Contains("角色头像读取失败")), "startup failure details remain available to diagnostic export");
        tabs.SelectedItem = startup.FindName("FilesTab");
        startup.UpdateLayout();
        var versionHint = (TextBlock)startup.FindName("ServerVersionHint");
        Check(versionHint.IsVisible && versionHint.ActualWidth > 200 && versionHint.ActualHeight > 0, "version and update panel is visible in real window file settings page");
        var screenshot = new RenderTargetBitmap((int)startup.ActualWidth, (int)startup.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        screenshot.Render(startup);
        var screenshots = Path.Combine(Directory.GetCurrentDirectory(), "work", "screenshots-0.3.7"); Directory.CreateDirectory(screenshots);
        var pngEncoder = new PngBitmapEncoder(); pngEncoder.Frames.Add(BitmapFrame.Create(screenshot));
        using (var imageFile = File.Create(Path.Combine(screenshots, "server-updates.png"))) pngEncoder.Save(imageFile);
        await VerifyWorldDeletionAsync(startup, worlds, screenshots);
        await (Task)typeof(MainWindow).GetMethod("FinishLogsAsync", flags)!.Invoke(startup, null)!;
        startup.Close();
    }

    private static IEnumerable<T> Children<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Children<T>(child)) yield return descendant;
        }
    }

    private static async Task VerifyWorldDeletionAsync(MainWindow window, string worlds, string screenshots)
    {
        WorldInfo Create(string folder, string name)
        {
            var path = Path.Combine(worlds, "fixture-account", folder);
            Directory.CreateDirectory(Path.Combine(path, "Master", "save"));
            Directory.CreateDirectory(Path.Combine(path, "Caves", "save"));
            File.WriteAllText(Path.Combine(path, "cluster.ini"), "[NETWORK]\ncluster_name=" + name + "\n[GAMEPLAY]\nmax_players=6\n");
            File.WriteAllText(Path.Combine(path, "Caves", "server.ini"), "[NETWORK]\nserver_port=11000\n");
            File.WriteAllText(Path.Combine(path, "Master", "save", "world.dat"), "precious fixture");
            return Discovery.ReadWorld(path);
        }
        var kept = Create("Cluster_1", "秋日篝火 · 保留世界");
        var deleted = Create("Cluster_2", "和朋友们一起冒险的世界");
        var list = (ListBox)window.FindName("WorldList");
        var overlay = (System.Windows.FrameworkElement)window.FindName("WorldDeleteOverlay");
        var cancel = (Button)window.FindName("CancelWorldDeleteButton");
        var confirm = (Button)window.FindName("ConfirmWorldDeleteButton");
        var workspace = (System.Windows.FrameworkElement)window.FindName("WorkspaceSurface");
        var tabs = (TabControl)window.FindName("Tabs"); tabs.SelectedItem = window.FindName("OverviewTab");
        await (Task)typeof(MainWindow).GetMethod("ScanAsync", flags)!.Invoke(window, [kept.Path])!;
        var settings = (AppSettings)typeof(MainWindow).GetField("settings", flags)!.GetValue(window)!;
        settings.Profiles[kept.Path] = new(); settings.Profiles[deleted.Path] = new();
        window.UpdateLayout();
        Button Trash(string path) => Children<Button>(list).Single(b => b.Name == "DeleteWorldButton" && b.DataContext is WorldInfo world && world.Path == path);
        bool Busy() => (bool)typeof(MainWindow).GetField("busy", flags)!.GetValue(window)!;
        async Task Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check(condition(), "world deletion interaction completes within its UI deadline");
        }
        void Click(Button button) => button.RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
        void Screenshot(string name)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(screenshots, name)); encoder.Save(output);
        }
        Check(Children<Button>(list).Count(b => b.Name == "DeleteWorldButton") == 2 && Equals(Trash(deleted.Path).ToolTip, "删除世界"), "each real world card has a trash icon with delete-world tooltip");
        Screenshot("world-cards.png");
        Click(Trash(deleted.Path)); await Until(() => overlay.IsVisible);
        Check(Directory.Exists(deleted.Path) && ((WorldInfo)list.SelectedItem).Path == kept.Path, "trash click opens confirmation without deleting or changing selected world");
        Check(!workspace.IsEnabled && ((TextBlock)window.FindName("DeleteWorldName")).Text == deleted.Name && Children<TextBlock>(overlay).Any(t => t.Text == "删除的世界将无法恢复。"), "themed confirmation names target, warns of permanent deletion and blocks background actions");
        Screenshot("delete-world-confirmation.png");
        Click(cancel); await Until(() => !Busy());
        Check(Directory.Exists(deleted.Path) && !overlay.IsVisible && workspace.IsEnabled, "cancel closes the dialog and leaves all world files intact");
        Click(Trash(deleted.Path)); await Until(() => overlay.IsVisible);
        Click(confirm); await Until(() => !Busy());
        Check(!Directory.Exists(deleted.Path) && Directory.Exists(kept.Path) && list.Items.Count == 1 && ((WorldInfo)list.SelectedItem).Path == kept.Path,
            "confirm permanently deletes only clicked world and refreshes cards while preserving selected survivor");
        Check(!settings.Profiles.ContainsKey(deleted.Path) && settings.Profiles.ContainsKey(kept.Path), "deletion clears only the deleted world's local profile");
        Click(Trash(kept.Path)); await Until(() => overlay.IsVisible);
        overlay.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(window)!, 0, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        await Until(() => !Busy());
        Check(Directory.Exists(kept.Path) && !overlay.IsVisible, "Escape cancels deletion without changing the save");
        typeof(MainWindow).GetField("busy", flags)!.SetValue(window, true);
        typeof(MainWindow).GetMethod("UpdateControls", flags)!.Invoke(window, null);
        Check(!Trash(kept.Path).IsEnabled, "trash button is unavailable while another operation owns the world controls");
        typeof(MainWindow).GetField("busy", flags)!.SetValue(window, false);
        typeof(MainWindow).GetMethod("UpdateControls", flags)!.Invoke(window, null);
        Click(Trash(kept.Path)); await Until(() => overlay.IsVisible);
        Click(confirm); await Until(() => !Busy());
        Check(!Directory.Exists(kept.Path) && list.Items.Count == 0 && list.SelectedItem is null && !((Button)window.FindName("StartButton")).IsEnabled,
            "deleting the last world leaves an empty overview and disables world startup");
    }
}
