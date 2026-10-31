using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private void SaveSmokeScreenshot(string path)
    {
        UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private async Task VerifyModWatchingSmokeAsync(string directory)
    {
        var oldWorlds = WorldList.Items.Cast<WorldInfo>().ToArray(); var oldWorld = CurrentWorld;
        var oldWorkshop = WorkshopPath.Text; var oldServer = ServerPath.Text;
        var fixture = Path.Combine(directory, "automatic-mod-fixture");
        var worldPath = Path.Combine(fixture, "Cluster_Auto");
        Directory.CreateDirectory(Path.Combine(worldPath, "Master"));
        File.WriteAllText(Path.Combine(worldPath, "cluster.ini"), "[NETWORK]\ncluster_name=Automatic discovery fixture\n");
        var configPath = Path.Combine(worldPath, "Master", "modoverrides.lua");
        const string config = "return { ['workshop-9002']={enabled=true} }";
        File.WriteAllText(configPath, config);
        var world = Discovery.ReadWorld(worldPath);
        var workshop = Path.Combine(fixture, "workshop");
        var exe = Path.Combine(fixture, "game", "bin64", "dontstarve_dedicated_server_nullrenderer_x64.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllText(exe, "fixture");
        async Task Until(Func<bool> ready)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
            while (!ready() && DateTimeOffset.UtcNow < deadline) await Task.Delay(100);
            if (!ready()) throw new InvalidOperationException("Automatic Mod detection timed out");
        }
        string Add(string id, bool client = false)
        {
            var path = Path.Combine(workshop, "content", "322330", id); Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "modinfo.lua"), $"name='Auto {id}' version='1' client_only_mod={client.ToString().ToLowerInvariant()} configuration_options={{{{name='flag',label='Flag',options={{{{description='On',data=true}},{{description='Off',data=false}}}},default=false}}}}");
            return path;
        }
        try
        {
            initialized = false; WorkshopPath.Text = workshop; ServerPath.Text = exe; WorldList.ItemsSource = new[] { world }; initialized = true;
            WorldList.SelectedItem = world; await modRefreshTask;
            var folder = Add("9001"); Add("9003", client: true);
            ModSearch.Text = "Auto"; ModFilter.SelectedIndex = 2;
            await Until(() => modCards.Any(c => c.Mod.Key == "workshop-9001"));
            if (installedMods.Any(m => m.ClientOnly) || ModSearch.Text != "Auto" || ModFilter.SelectedIndex != 2 || modSnapshot?.AnyEnabled("workshop-9001") == true) throw new InvalidOperationException("Automatic add changed filters, enabled a new Mod or exposed client mods");
            var windows = Application.Current.Windows.Count;
            var sample = installedMods.Single(m => m.Key == "workshop-9001");
            var editor = new ModSettingsPanel(sample, modSnapshot!, (_, _) => throw new InvalidOperationException("No write allowed"));
            if (editor.FindName("TargetShard") is not null || editor.FindName("OptionSearch") is not null) throw new InvalidOperationException("Removed configuration controls remain");
            var overlay = ShowModSettingsAsync(editor); editor.CheckSmoke();
            Add("9004"); await Task.Delay(3200);
            if (installedMods.Any(m => m.Key == "workshop-9004") || Application.Current.Windows.Count != windows || WorkspaceSurface.IsEnabled) throw new InvalidOperationException("Mod overlay did not defer background changes or opened another window");
            editor.Dismiss(); await overlay;
            await Until(() => installedMods.Any(m => m.Key == "workshop-9004"));
            Directory.Delete(folder, recursive: true);
            await Until(() => installedMods.All(m => m.Key != "workshop-9001"));
            var configuredFolder = Add("9002");
            await Until(() => installedMods.Any(m => m.Key == "workshop-9002" && m.Downloaded));
            Directory.Delete(configuredFolder, recursive: true);
            await Until(() => modCards.Any(c => c.Mod.Key == "workshop-9002" && !c.Mod.Downloaded));
            if (File.ReadAllText(configPath) != config) throw new InvalidOperationException("Automatic detection wrote world configuration");
            File.WriteAllText(Path.Combine(directory, "automatic-mod-report.json"), "{\"timerDrivenAdd\":true,\"timerDrivenDelete\":true,\"configuredMissing\":true,\"clientExcluded\":true,\"overlayDeferred\":true,\"filtersPreserved\":true,\"noConfigurationWrites\":true}");
        }
        finally
        {
            initialized = false; WorkshopPath.Text = oldWorkshop; ServerPath.Text = oldServer; WorldList.ItemsSource = oldWorlds; initialized = true;
            WorldList.SelectedItem = oldWorld; await modRefreshTask;
            ModSearch.Clear(); ModFilter.SelectedIndex = 0;
        }
    }

    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in VisualChildren<T>(child)) yield return nested;
        }
    }
}
