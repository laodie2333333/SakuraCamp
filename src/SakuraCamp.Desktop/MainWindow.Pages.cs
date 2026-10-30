using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private readonly DispatcherTimer modWatchTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string modFingerprint = "";
    private string modCandidate = "";
    private bool modPolling;
    private bool modWatchClosed;
    private DateTimeOffset modRetryAt = DateTimeOffset.MaxValue;
    private ModSettingsWindow? activeModSettings;

    private void InitializeModWatching()
    {
        modWatchTimer.Tick += async (_, _) => await PollModsAsync();
        modWatchTimer.Start();
        Closed += (_, _) => { modWatchClosed = true; modWatchTimer.Stop(); ++modScanGeneration; };
    }

    private static string ModFingerprint(string workshop, string server, WorldInfo world)
    {
        var result = ModCatalog.Fingerprint(workshop, server) + "|" + world.Path;
        foreach (var shard in new[] { "Master", "Caves" })
        {
            var file = new FileInfo(Path.Combine(world.Path, shard, "modoverrides.lua"));
            result += file.Exists ? $"|{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "|missing";
        }
        return result;
    }

    private async Task PollModsAsync()
    {
        if (!initialized || busy || modPolling || !modRefreshTask.IsCompleted || activeModSettings is not null || CurrentWorld is not { } world || modWatchClosed) return;
        modPolling = true;
        var workshop = WorkshopPath.Text.Trim(); var server = ServerPath.Text.Trim();
        try
        {
            var stamp = await Task.Run(() => ModFingerprint(workshop, server, world));
            if (busy || activeModSettings is not null || CurrentWorld?.Path != world.Path || !modRefreshTask.IsCompleted || modWatchClosed) return;
            if (stamp == modFingerprint && DateTimeOffset.UtcNow < modRetryAt) { modCandidate = ""; return; }
            // Wait for two matching samples so Steam's partial writes settle first.
            if (stamp != modCandidate) { modCandidate = stamp; return; }
            modRefreshTask = RefreshModManagerAsync(world, automatic: true);
            await modRefreshTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keep the previous cards and retry. Background changes must not open modal alerts.
            modCandidate = "";
        }
        finally { modPolling = false; }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || WorldSidebar is null || OverviewTab is null) return;
        var overview = Tabs.SelectedItem == OverviewTab;
        WorldSidebar.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        WorldSidebarColumn.Width = new GridLength(overview ? 270 : 0);
    }

    private async Task ShowModSettingsAsync(ModSettingsWindow editor)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previousFocus = Keyboard.FocusedElement;
        activeModSettings = editor;
        ModOverlayContent.Content = editor;
        WorkspaceSurface.IsEnabled = false;
        ModOverlay.Visibility = Visibility.Visible;
        ModOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        ModOverlayPanel.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        void Dismiss() => completion.TrySetResult();
        editor.Dismissed += Dismiss;
        editor.Focus();
        try { await completion.Task; }
        finally
        {
            editor.Dismissed -= Dismiss;
            ModOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(120)));
            await Task.Delay(120);
            ModOverlay.Visibility = Visibility.Collapsed; ModOverlayContent.Content = null;
            activeModSettings = null; WorkspaceSurface.IsEnabled = true;
            if (previousFocus is not null) Keyboard.Focus(previousFocus);
        }
    }

    private void ModOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { activeModSettings?.Dismiss(); e.Handled = true; }
    }
}
