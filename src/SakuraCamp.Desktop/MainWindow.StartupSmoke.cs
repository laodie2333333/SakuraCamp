using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private async Task VerifyStartupResponsivenessSmokeAsync(string directory)
    {
        var oldTab = Tabs.SelectedItem;
        var originalWorlds = WorldList.ItemsSource; var originalWorld = CurrentWorld;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation = Task.CompletedTask;
        var path = Path.Combine(directory, "startup-ui-fixture"); Directory.CreateDirectory(Path.Combine(path, "Master"));
        File.WriteAllText(Path.Combine(path, "cluster.ini"), "[NETWORK]\ncluster_name=启动响应检查\ncluster_password=fixture-password\n");
        var fixture = Discovery.ReadWorld(path);
        try
        {
            Tabs.SelectedItem = OverviewTab; WorldList.ItemsSource = new[] { fixture }; WorldList.SelectedItem = fixture; await modRefreshTask;
            Tabs.SelectedItem = ConnectionTab;
            operation = Guard(async () => { startingWorld = true; UpdateControls(); await completion.Task; });
            if (!busy || ConnectionFields.IsEnabled || !CopyFriendButton.IsEnabled || !Tabs.IsEnabled || WorldList.IsEnabled)
                throw new InvalidOperationException("Startup locked read-only UI or left configuration editable");
            MasterAddress.Text = "frp-example.com:44812";
            string copied = ""; clipboardSmokeAction = value => copied = value;
            await CopyConnect(false);
            if (!copied.Contains("fixture-password") || !copied.Contains("frp-example.com")) throw new InvalidOperationException("Friend command could not be copied while startup was pending");
            var measurements = new List<object>();
            foreach (var tab in new[] { ConnectionTab, DiagnosticsTab })
            {
                Tabs.SelectedItem = tab; UpdateLayout();
                var queued = 0L; var handled = 0L; var rendered = logRenderCount;
                await Task.Run(async () =>
                {
                    for (var index = 0; index < 5000; index++) session.Emit($"[Master] startup stress {index}: " + new string('x', 160));
                    queued = Stopwatch.GetTimestamp();
                    await Dispatcher.InvokeAsync(() =>
                    {
                        handled = Stopwatch.GetTimestamp(); Tabs.SelectedItem = OverviewTab;
                    }, DispatcherPriority.Input).Task;
                });
                var wait = (handled - queued) * 1000d / Stopwatch.Frequency;
                if (wait > 300 || Tabs.SelectedItem != OverviewTab || logRenderCount - rendered > 5)
                    throw new InvalidOperationException($"Startup log burst blocked UI input: {wait:0.0} ms");
                DrainPendingLogs(int.MaxValue);
                if (!logLines.Last().Contains("4999")) throw new InvalidOperationException("Log backlog did not retain the most recent output");
                measurements.Add(new { Page = tab.Header, Lines = 5000, InputWaitMs = wait, RenderCount = logRenderCount - rendered });
            }
            Tabs.SelectedItem = DiagnosticsTab; UpdateLayout();
            var delays = new List<double>(); var beforeRender = logRenderCount;
            await Task.Run(async () =>
            {
                for (var batch = 0; batch < 20; batch++)
                {
                    for (var line = 0; line < 80; line++) session.Emit($"[Master] sustained startup {batch}/{line}");
                    var start = Stopwatch.GetTimestamp();
                    await Dispatcher.InvokeAsync(() => delays.Add((Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency), DispatcherPriority.Input).Task;
                    await Task.Delay(100);
                }
            });
            if (delays.Max() > 500 || logRenderCount - beforeRender > 30 || logRenderCount == beforeRender)
                throw new InvalidOperationException($"Visible live log rendering delayed input: {delays.Max():0} ms / {logRenderCount - beforeRender} refreshes");
            File.WriteAllText(Path.Combine(directory, "startup-response-report.json"), System.Text.Json.JsonSerializer.Serialize(new
            { PendingStartup = true, Navigation = true, FriendCopy = true, ConfigurationLocked = true, Logs = measurements, SustainedLines = 1600, VisibleLogRenders = logRenderCount - beforeRender, MaxSustainedInputWaitMs = delays.Max(), RealServerStarted = false }));
        }
        finally
        {
            clipboardSmokeAction = null; startingWorld = false; completion.TrySetResult(); await operation;
            WorldList.ItemsSource = originalWorlds; WorldList.SelectedItem = originalWorld; await modRefreshTask;
            Tabs.SelectedItem = oldTab; UpdateControls();
        }
    }
    private async Task VerifyDropdownAndLogThumbSmokeAsync(string directory)
    {
        var oldItems = CreationNode.ItemsSource; var oldNode = CreationNode.SelectedItem; var oldTab = Tabs.SelectedItem;
        var oldWidth = Width; var oldHeight = Height; var expanded = TunnelLogExpander.IsExpanded;
        var thumbChecks = new List<object>();
        double wheelOffset = 0;
        suppressNodeSelection++;
        try
        {
            Tabs.SelectedItem = ConnectionTab; UpdateLayout();
            CreationNode.ItemsSource = Enumerable.Range(1, 100).Select(i => new NodeInfo(i, $"滚轮检查节点 {i:000}", "node.example.com", 0, 36)).ToArray();
            CreationNode.SelectedIndex = 0; CreationNode.IsDropDownOpen = true;
            await Task.Delay(250); UpdateLayout();
            var popup = (Popup)CreationNode.Template.FindName("Popup", CreationNode);
            var scroll = VisualChildren<ScrollViewer>(popup.Child).Single();
            if (scroll.CanContentScroll) throw new InvalidOperationException("Dropdown still interprets pixels as item counts");
            var first = (ComboBoxItem)CreationNode.ItemContainerGenerator.ContainerFromIndex(0);
            first.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Task.Delay(500); scroll.UpdateLayout(); wheelOffset = scroll.VerticalOffset;
            if (SystemParameters.WheelScrollLines != 0 && (wheelOffset <= 0 || wheelOffset >= scroll.ScrollableHeight || wheelOffset > scroll.ViewportHeight + 2))
                throw new InvalidOperationException($"One wheel step skipped dropdown contents: {wheelOffset}/{scroll.ScrollableHeight}");
            if (CreationNode.SelectedIndex != 0) throw new InvalidOperationException("Scrolling the open dropdown changed selection");
            var image = new RenderTargetBitmap((int)popup.Child.RenderSize.Width, (int)popup.Child.RenderSize.Height, 96, 96, PixelFormats.Pbgra32); image.Render(popup.Child);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(directory, "node-dropdown.png"))) encoder.Save(output);
            CreationNode.IsDropDownOpen = false;
            Tabs.SelectedItem = DiagnosticsTab;
            for (var i = 0; i < 650; i++) AppendLog($"[Master] scrollbar fixture {i}: " + new string('x', 140));
            foreach (var size in new[] { (1220d, 850d), (MinWidth, MinHeight) })
            foreach (var open in new[] { false, true })
            {
                Width = size.Item1; Height = size.Item2; TunnelLogExpander.IsExpanded = open; FlushLogViews(); UpdateLayout();
                LogBox.ScrollToEnd(); await Task.Delay(80); UpdateLayout();
                var bar = VisualChildren<ScrollBar>(LogBox).Single(b => b.Orientation == Orientation.Vertical);
                var track = (Track)bar.Template.FindName("PART_Track", bar); var thumb = track.Thumb;
                var bounds = thumb.TransformToAncestor(track).TransformBounds(new Rect(thumb.RenderSize));
                if (bounds.Top < -1 || bounds.Bottom > track.ActualHeight + 1 || thumb.ActualHeight < 23)
                    throw new InvalidOperationException($"Log thumb clipped after expanding tunnels: {bounds}/{track.ActualHeight}");
                var handle = (Border)thumb.Template.FindName("Handle", thumb);
                var pixels = RenderInput(handle); var width = (int)handle.ActualWidth; var height = (int)handle.ActualHeight;
                int Alpha(int x, int y) => pixels[(y * width + x) * 4 + 3];
                if (Alpha(width / 2, 0) < 100 || Alpha(width / 2, height - 1) < 100 || Alpha(0, 0) > 100 || Alpha(0, height - 1) > 100)
                    throw new InvalidOperationException("Log handle lost a rounded end");
                thumbChecks.Add(new { Width = size.Item1, TunnelsExpanded = open, ThumbHeight = thumb.ActualHeight, TrackHeight = track.ActualHeight, Bottom = bounds.Bottom });
                SaveSmokeScreenshot(Path.Combine(directory, $"logs-{size.Item1:0}-{open}.png"));
            }
            File.WriteAllText(Path.Combine(directory, "dropdown-scroll-report.json"), System.Text.Json.JsonSerializer.Serialize(new
            { PixelScrolling = true, OneNotchOffset = wheelOffset, Items = 100, SelectionPreserved = true, LogThumbChecks = thumbChecks }));
        }
        finally
        {
            CreationNode.IsDropDownOpen = false; CreationNode.ItemsSource = oldItems; CreationNode.SelectedItem = oldNode; suppressNodeSelection--;
            Width = oldWidth; Height = oldHeight; TunnelLogExpander.IsExpanded = expanded; Tabs.SelectedItem = oldTab;
        }
    }
}
