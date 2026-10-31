using System.IO;
using System.Windows;
using System.Windows.Controls;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private async Task VerifyNodeSelectionSmokeAsync()
    {
        var oldNodes = creationNodes; var oldItems = CreationNode.ItemsSource; var oldNode = CreationNode.SelectedItem; var count = 0;
        try
        {
            nodeSelectionSmokeAction = _ => { count++; return Task.CompletedTask; };
            creationNodes = [new(701, "Fixture A", "a.example.com", 0, 4 | 32), new(702, "Fixture B", "b.example.com", 0, 4 | 32)];
            RestoreWorldNode(new() { CreationNodeId = 701 }); await nodeSelectionTask;
            if (count != 0) throw new InvalidOperationException("Restoring saved node triggered tunnel replacement");
            CreationNode.SelectedValue = 702; await nodeSelectionTask;
            if (count != 1) throw new InvalidOperationException("User node selection did not trigger automatic configuration once");
            RestoreWorldNode(new() { CreationNodeId = 702 }); await nodeSelectionTask;
            if (count != 1) throw new InvalidOperationException("List refresh triggered tunnel replacement");
        }
        finally
        {
            nodeSelectionSmokeAction = null; creationNodes = oldNodes;
            suppressNodeSelection++;
            try { CreationNode.ItemsSource = oldItems; CreationNode.SelectedItem = oldNode; }
            finally { suppressNodeSelection--; }
        }
    }

    private void VerifySavedSettingsSmoke(string directory)
    {
        var oldToken = KleiToken.Password; var oldPassword = WorldPassword.Password; var oldHint = TokenHint.Text;
        var oldNodes = creationNodes; var oldItems = CreationNode.ItemsSource; var oldSelection = CreationNode.SelectedItem;
        var fixture = Path.Combine(directory, "saved-connection-fixture");
        Directory.CreateDirectory(Path.Combine(fixture, "Master"));
        File.WriteAllText(Path.Combine(fixture, "cluster.ini"), "[NETWORK]\ncluster_name=Connection fixture\ncluster_password=optional-test-password\n");
        const string token = "pds-fixture-token-do-not-use-123456";
        File.WriteAllText(Path.Combine(fixture, "cluster_token.txt"), token);
        try
        {
            var world = Discovery.ReadWorld(fixture);
            LoadWorldCredentials(world);
            if (KleiToken.Password != token || WorldPassword.Password != "optional-test-password") throw new InvalidOperationException("Saved credentials did not populate inputs");
            KleiToken.Clear(); LoadWorldCredentials(world);
            if (KleiToken.Password != token) throw new InvalidOperationException("Reload cleared saved token");
            File.WriteAllText(Path.Combine(fixture, "cluster.ini"), "[NETWORK]\ncluster_name=Connection fixture\ncluster_password=\n");
            LoadWorldCredentials(world);
            if (WorldPassword.Password.Length != 0 || KleiToken.Password != token) throw new InvalidOperationException("Optional password altered required token");
            var profile = new WorldProfile { CreationNodeId = 42, MasterAddress = "saved.example.com:12345" };
            creationNodes = []; RestoreWorldNode(profile);
            if (CreationNode.SelectedItem is not NodeInfo { Id: 42 } || CreationNode.Text.Length == 0) throw new InvalidOperationException("Legacy node ID restored as blank selection");
            profile.SavedNode = new NodeInfo(42, "已保存的节点名称", "saved.example.com", 0, 4 | 32);
            var roundtrip = System.Text.Json.JsonSerializer.Deserialize<WorldProfile>(System.Text.Json.JsonSerializer.Serialize(profile))!;
            RestoreWorldNode(roundtrip);
            if ((CreationNode.SelectedItem as NodeInfo)?.Name != "已保存的节点名称") throw new InvalidOperationException("Saved node name lost after settings reload");
            creationNodes = [new NodeInfo(42, "更新后的节点名称", "saved.example.com", 0, 4 | 32)]; RestoreWorldNode(roundtrip);
            if ((CreationNode.SelectedItem as NodeInfo)?.Name != "更新后的节点名称") throw new InvalidOperationException("Fresh node metadata did not supersede saved name");
            Tabs.SelectedItem = ConnectionTab; UpdateLayout();
            SaveSmokeScreenshot(Path.Combine(directory, "saved-connection-fields.png"));
            var tab = Tabs.SelectedItem; var errorFile = Path.Combine(directory, "error.txt");
            ShowError(new InvalidOperationException("Expected offline settings error"));
            if (Tabs.SelectedItem != tab || !logLines.Any(line => line.Contains("Expected offline settings error"))) throw new InvalidOperationException("Error changed selected page or lost diagnostic logging");
            File.Delete(errorFile);
            File.WriteAllText(Path.Combine(directory, "saved-settings-report.json"), "{\"savedTokenPopulated\":true,\"savedNodePopulated\":true,\"legacyNodeVisible\":true,\"nodeNameRoundTrip\":true,\"optionalPassword\":true,\"errorKeepsPage\":true}");
        }
        finally
        {
            KleiToken.Password = oldToken; WorldPassword.Password = oldPassword;
            TokenHint.Text = oldHint;
            creationNodes = oldNodes; CreationNode.ItemsSource = oldItems; CreationNode.SelectedItem = oldSelection;
        }
    }

    private static void VerifyModTooltipSmoke(DependencyObject parent, bool allowGear)
    {
        foreach (var element in VisualChildren<FrameworkElement>(parent))
        {
            if (element.ToolTip is null) continue;
            if (!allowGear || element is not Button || element.ToolTip as string != "配置 Mod")
                throw new InvalidOperationException("Unrequested tooltip in Mod cards or configuration options");
        }
        if (VisualChildren<TextBlock>(parent).Any(t => t.Text.StartsWith("工坊 ", StringComparison.Ordinal)))
            throw new InvalidOperationException("Workshop ID remains on Mod card");
    }
}
