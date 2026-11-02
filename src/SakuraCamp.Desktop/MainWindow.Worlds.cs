using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private TaskCompletionSource<bool>? deleteWorldConfirmation;

    private async void DeleteWorld_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: WorldInfo world })
            await Guard(() => DeleteWorldAsync(world));
    }

    private async Task DeleteWorldAsync(WorldInfo world)
    {
        RequireIdle();
        var root = RootPath.Text.Trim();
        await Task.Run(() => WorldDeletion.Validate(world, root));
        if (!await ConfirmWorldDeletionAsync(world)) return;
        RequireIdle();
        var previousSelection = CurrentWorld?.Path;
        StatusText.Text = "正在删除世界…";
        try
        {
            await Task.Run(() => WorldDeletion.Delete(world, root));
            foreach (var key in settings.Profiles.Keys.Where(p => p.Equals(world.Path, StringComparison.OrdinalIgnoreCase)).ToArray())
                settings.Profiles.Remove(key);
            if (settings.LastWorld.Equals(world.Path, StringComparison.OrdinalIgnoreCase)) settings.LastWorld = "";
            await RefreshWorldsAfterDeletionAsync(root, previousSelection);
            StatusText.Text = "已删除世界：“" + world.Name + "”。";
            AppendLog("[世界删除] 已确认并永久删除：" + world.Path);
        }
        catch
        {
            // A filesystem error may leave a partially removed world. Reflect the disk state.
            await RefreshWorldsAfterDeletionAsync(root, previousSelection);
            AppendLog("[世界删除] 删除未完成，请检查目标目录：" + world.Path);
            throw;
        }
    }

    private async Task RefreshWorldsAfterDeletionAsync(string root, string? previousSelection)
    {
        var worlds = await Task.Run(() => Discovery.Worlds(root));
        var selected = worlds.FirstOrDefault(w => w.Path.Equals(previousSelection, StringComparison.OrdinalIgnoreCase)) ?? worlds.FirstOrDefault();
        var selectionUnchanged = selected is not null && selected.Path.Equals(previousSelection, StringComparison.OrdinalIgnoreCase);
        var wasInitialized = initialized;
        initialized = false;
        try { WorldList.ItemsSource = worlds; WorldList.SelectedItem = selected; }
        finally { initialized = wasInitialized; }
        WorldCount.Text = worlds.Count > 0 ? $"发现 {worlds.Count} 个本地世界" : "未找到存档 · 可导入已有世界";
        if (!selectionUnchanged)
        {
            ResetWorldRules();
            WorldList_SelectionChanged(WorldList, new SelectionChangedEventArgs(Selector.SelectionChangedEvent, Array.Empty<WorldInfo>(), Array.Empty<WorldInfo>()));
            await modRefreshTask;
            if (selected is null)
            {
                WorldTitle.Text = "把朋友带进同一个世界";
                WorldSubtitle.Text = "选择一个存档，设置一次，以后直接开服。";
                ResetLiveStatus();
            }
        }
        Persist(); UpdateControls();
    }

    private async Task<bool> ConfirmWorldDeletionAsync(WorldInfo world)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousFocus = Keyboard.FocusedElement;
        var workspaceEnabled = WorkspaceSurface.IsEnabled;
        deleteWorldConfirmation = completion;
        DeleteWorldName.Text = world.Name;
        DeleteWorldFolder.Text = world.Folder;
        WorkspaceSurface.IsEnabled = false;
        WorldDeleteOverlay.Visibility = Visibility.Visible;
        CancelWorldDeleteButton.Focus();
        try { return await completion.Task; }
        finally
        {
            WorldDeleteOverlay.Visibility = Visibility.Collapsed;
            deleteWorldConfirmation = null;
            WorkspaceSurface.IsEnabled = workspaceEnabled;
            if (previousFocus is not null) Keyboard.Focus(previousFocus);
        }
    }

    private void ConfirmWorldDelete_Click(object sender, RoutedEventArgs e) => deleteWorldConfirmation?.TrySetResult(true);
    private void CancelWorldDelete_Click(object sender, RoutedEventArgs e) => deleteWorldConfirmation?.TrySetResult(false);
    private void WorldDeleteOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { deleteWorldConfirmation?.TrySetResult(false); e.Handled = true; }
    }
}
