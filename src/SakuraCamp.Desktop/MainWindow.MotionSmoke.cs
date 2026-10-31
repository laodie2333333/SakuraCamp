using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private async Task VerifyMotionSmokeAsync(string directory)
    {
        var oldTab = Tabs.SelectedItem;
        try
        {
            if (WindowChrome.GetWindowChrome(this) is not { CaptionHeight: 85, UseAeroCaptionButtons: false } || !WindowChrome.GetIsHitTestVisibleInChrome(CloseCaption))
                throw new InvalidOperationException("Integrated caption or window button hit testing missing");
            MaximizeCaption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200); UpdateLayout();
            if (WindowState != WindowState.Maximized || MaximizeGlyph.Text != "\uE923") throw new InvalidOperationException("Custom maximize button failed");
            SaveSmokeScreenshot(Path.Combine(directory, "maximized.png"));
            MaximizeCaption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(200); UpdateLayout();
            if (WindowState != WindowState.Normal) throw new InvalidOperationException("Custom restore button failed");
            MinimizeCaption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(120);
            if (WindowState != WindowState.Minimized) throw new InvalidOperationException("Custom minimize button failed");
            SystemCommands.RestoreWindow(this); await Task.Delay(150); UpdateLayout();
            var previousStatus = StatusText.Text;
            busy = true;
            try { CloseCaption.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150); if (!IsVisible) throw new InvalidOperationException("Close button bypassed closing guard"); }
            finally { busy = false; StatusText.Text = previousStatus; UpdateControls(); }
            Tabs.SelectedItem = ModManagerTab; UpdateLayout();
            var hoverTrigger = ModManagerTab.Template.Triggers.OfType<Trigger>().Single(t => t.Property == IsMouseOverProperty);
            var hoverIn = hoverTrigger.EnterActions.OfType<BeginStoryboard>().Single().Storyboard;
            var hoverOut = hoverTrigger.ExitActions.OfType<BeginStoryboard>().Single().Storyboard;
            var wash = (Border)ModManagerTab.Template.FindName("HoverWash", ModManagerTab);
            hoverIn.Begin(ModManagerTab, ModManagerTab.Template, HandoffBehavior.SnapshotAndReplace, true);
            await Task.Delay(80); var fading = wash.Opacity; await Task.Delay(180);
            if (fading <= 0 || fading >= 0.7 || Math.Abs(wash.Opacity - 0.7) > 0.01) throw new InvalidOperationException("Navigation hover animation did not interpolate");
            SaveSmokeScreenshot(Path.Combine(directory, "navigation-hover.png"));
            hoverOut.Begin(ModManagerTab, ModManagerTab.Template, HandoffBehavior.SnapshotAndReplace, true); await Task.Delay(250);
            if (wash.Opacity > 0.01) throw new InvalidOperationException("Navigation hover animation did not fade out");
            if (VisualChildren<TextBlock>(ModList).Any(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "Description"))
                throw new InvalidOperationException("Mod descriptions still present in cards");
            if (!SmoothScroll.GetEnabled(ModScroll)) throw new InvalidOperationException("Wheel smoothing not attached to Mod page");
            ModScroll.ScrollToTop(); UpdateLayout();
            void Wheel(int delta) => ModList.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Wheel(-120);
            var instant = ModScroll.VerticalOffset;
            await Task.Delay(65); UpdateLayout(); var intermediate = ModScroll.VerticalOffset;
            var step = SystemParameters.WheelScrollLines < 0 ? ModScroll.ViewportHeight : SystemParameters.WheelScrollLines * 20d;
            if (SystemParameters.ClientAreaAnimation && step > 0 && (instant != 0 || intermediate <= 0 || intermediate >= step))
                throw new InvalidOperationException($"Wheel did not interpolate: {instant}/{intermediate}/{step}");
            Wheel(-120); Wheel(-120); await Task.Delay(550); UpdateLayout();
            if (Math.Abs(ModScroll.VerticalOffset - Math.Min(step * 3, ModScroll.ScrollableHeight)) > 1) throw new InvalidOperationException("Consecutive wheel input lost distance");
            Wheel(120); await Task.Delay(500); UpdateLayout();
            if (Math.Abs(ModScroll.VerticalOffset - Math.Min(step * 2, ModScroll.ScrollableHeight)) > 1) throw new InvalidOperationException("Reverse wheel input failed");
            Wheel(-120); Tabs.SelectedItem = OverviewTab; await Task.Delay(150);
            Tabs.SelectedItem = ModManagerTab; UpdateLayout(); var paused = ModScroll.VerticalOffset;
            await Task.Delay(150); UpdateLayout();
            if (Math.Abs(ModScroll.VerticalOffset - paused) > 1) throw new InvalidOperationException("Hidden page continued scrolling");
            ModScroll.ScrollToTop(); UpdateLayout(); Wheel(-120); await Task.Delay(50);
            ModScroll.ScrollToVerticalOffset(250); await Task.Delay(200); UpdateLayout();
            if (Math.Abs(ModScroll.VerticalOffset - 250) > 1) throw new InvalidOperationException("Wheel animation overrode an explicit scroll");
            ModScroll.ScrollToTop(); UpdateLayout();
            SaveSmokeScreenshot(Path.Combine(directory, "compact-mods.png"));
            Tabs.SelectedItem = ConnectionTab; UpdateLayout();
            var formScroll = FindVisualParent<ScrollViewer>(ConnectionFields)!;
            formScroll.ScrollToTop(); UpdateLayout(); var savedNode = CreationNode.SelectedItem;
            CreationNode.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Task.Delay(350); UpdateLayout();
            if (CreationNode.SelectedItem != savedNode) throw new InvalidOperationException("Wheel changed selected node");
            if (formScroll.ScrollableHeight > 0 && SystemParameters.WheelScrollLines != 0 && formScroll.VerticalOffset <= 0) throw new InvalidOperationException("Input wheel did not reach parent scroll viewer");
            formScroll.ScrollToTop();
            File.WriteAllText(Path.Combine(directory, "motion-report.json"), System.Text.Json.JsonSerializer.Serialize(new
            { IntegratedCaption = true, MaximizeRestoreMinimize = true, ModDescriptionsRemoved = true, SmoothWheel = true, InstantOffset = instant,
                IntermediateOffset = intermediate, WheelDistance = step, ConsecutiveInput = true, ReverseInput = true, HiddenPageStops = true, ExplicitScrollInterrupts = true,
                NavigationHover = true, InputWheelPreservesNode = true, CloseGuardPreserved = true }));
        }
        finally { if (WindowState != WindowState.Normal) SystemCommands.RestoreWindow(this); Tabs.SelectedItem = oldTab; }
    }
}
