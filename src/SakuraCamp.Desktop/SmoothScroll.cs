using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SakuraCamp.Desktop;

// Animate only wheel input. Thumb dragging, keyboard navigation, touch and application
// ScrollTo calls retain their native behavior and can interrupt a pending wheel movement.
internal static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, EnabledChanged));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    private static readonly ConditionalWeakTable<ScrollViewer, Motion> motions = new();

    private static void EnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ScrollViewer viewer) return;
        if ((bool)e.NewValue)
        {
            viewer.PreviewMouseWheel += Wheel;
            viewer.PreviewMouseDown += CancelMouse;
            viewer.PreviewKeyDown += CancelKey;
            viewer.Unloaded += CancelUnload;
            viewer.IsVisibleChanged += VisibilityChanged;
        }
        else
        {
            viewer.PreviewMouseWheel -= Wheel;
            viewer.PreviewMouseDown -= CancelMouse;
            viewer.PreviewKeyDown -= CancelKey;
            viewer.Unloaded -= CancelUnload;
            viewer.IsVisibleChanged -= VisibilityChanged;
            Stop(viewer);
        }
    }

    private static DependencyObject? Parent(DependencyObject item) => item is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);

    private static ScrollViewer? WheelTarget(DependencyObject source)
    {
        DependencyObject? current = source;
        // Keep wheel input over editable fields from changing their value or scrolling
        // their internal text. Read-only logs and open dropdowns scroll their own content.
        for (var ancestor = source; ancestor is not null; ancestor = Parent(ancestor))
        {
            if (ancestor is ComboBox { IsDropDownOpen: false } or PasswordBox or TextBox { IsReadOnly: false })
            { current = Parent(ancestor); break; }
        }
        for (; current is not null; current = Parent(current))
            if (current is ScrollViewer { IsEnabled: true, IsVisible: true, ScrollableHeight: > 0 } viewer && GetEnabled(viewer)) return viewer;
        return null;
    }

    private static void Wheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.OriginalSource is not DependencyObject source || WheelTarget(source) is not { } viewer) return;
        var lines = SystemParameters.WheelScrollLines;
        if (lines == 0) return;
        var distance = lines < 0 ? viewer.ViewportHeight : lines * 20d;
        var motion = motions.GetValue(viewer, v => new Motion(v));
        motion.Move(-e.Delta / 120d * distance);
        e.Handled = true;
    }

    private static void Stop(object sender) { if (sender is ScrollViewer viewer && motions.TryGetValue(viewer, out var motion)) motion.Stop(); }
    private static void CancelMouse(object sender, MouseButtonEventArgs e) => Stop(sender);
    private static void CancelKey(object sender, KeyEventArgs e) => Stop(sender);
    private static void CancelUnload(object sender, RoutedEventArgs e) => Stop(sender);
    private static void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Stop(sender); }

    private sealed class Motion
    {
        private readonly ScrollViewer viewer;
        private bool active;
        private double target;
        private double requested;
        private long lastFrame;
        public Motion(ScrollViewer viewer)
        {
            this.viewer = viewer;
            viewer.ScrollChanged += (_, e) =>
            {
                if (active && e.VerticalChange != 0 && Math.Abs(e.VerticalOffset - requested) > 1) Stop();
            };
        }
        public void Move(double distance)
        {
            target = Math.Clamp((active ? target : viewer.VerticalOffset) + distance, 0, viewer.ScrollableHeight);
            if (!SystemParameters.ClientAreaAnimation)
            { Stop(); viewer.ScrollToVerticalOffset(target); return; }
            if (active || Math.Abs(target - viewer.VerticalOffset) < 0.1) return;
            requested = viewer.VerticalOffset;
            lastFrame = Stopwatch.GetTimestamp(); active = true;
            CompositionTarget.Rendering += Frame;
        }
        private void Frame(object? sender, EventArgs e)
        {
            if (!viewer.IsVisible || !viewer.IsEnabled) { Stop(); return; }
            var now = Stopwatch.GetTimestamp();
            var seconds = Math.Clamp((now - lastFrame) / (double)Stopwatch.Frequency, 0, 0.05);
            lastFrame = now;
            target = Math.Clamp(target, 0, viewer.ScrollableHeight);
            var remaining = target - viewer.VerticalOffset;
            requested = Math.Abs(remaining) < 0.4 ? target : viewer.VerticalOffset + remaining * (1 - Math.Exp(-seconds / 0.045));
            viewer.ScrollToVerticalOffset(requested);
            if (requested == target) Stop();
        }
        public void Stop() { if (!active) return; active = false; CompositionTarget.Rendering -= Frame; }
    }
}
