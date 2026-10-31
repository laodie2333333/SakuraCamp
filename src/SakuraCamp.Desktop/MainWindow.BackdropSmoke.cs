using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private async Task VerifyBackdropSmokeAsync(string directory)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Native move/size messages must leave both the effect and the tint untouched.
        var originalBackground = WindowSurface.Background;
        SmokeSendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
        if (!ReferenceEquals(originalBackground, WindowSurface.Background))
            throw new InvalidOperationException("Background changed at the start of native movement");
        var oldLeft = Left; var oldTop = Top; var oldWidth = Width; var oldHeight = Height;
        SmokeGetWindowRect(handle, out var bounds);
        var elapsed = Stopwatch.StartNew();
        try
        {
            for (var index = 0; index < 60; index++)
            {
                if (index < 30) SmokeSetWindowPos(handle, IntPtr.Zero, bounds.Left + index % 10, bounds.Top, 0, 0, 0x0015);
                else SmokeSetWindowPos(handle, IntPtr.Zero, 0, 0, bounds.Right - bounds.Left + index % 10, bounds.Bottom - bounds.Top, 0x0016);
                if (!ReferenceEquals(originalBackground, WindowSurface.Background)) throw new InvalidOperationException("Background changed during native move/resize");
                await Task.Delay(1);
            }
        }
        finally
        {
            Left = oldLeft; Top = oldTop; Width = oldWidth; Height = oldHeight;
            SmokeSendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
        }
        elapsed.Stop();
        if (!ReferenceEquals(originalBackground, WindowSurface.Background) || (backdropEnabled && WindowSurface.Background is not SolidColorBrush { Color.A: < 200 }))
            throw new InvalidOperationException("Moving the window replaced its translucent background");
        uint first = 0, second = 0; var difference = 0; var rawStripeContrast = 0; var blurredStripeContrast = 0; var screenOccluded = false;
        if (backdropEnabled)
        {
            var wasTopmost = Topmost;
            var behind = new Window { Topmost = false, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                ShowActivated = false, Left = Left, Top = Top, Width = ActualWidth, Height = ActualHeight, Background = new SolidColorBrush(Color.FromRgb(20, 190, 210)) };
            try
            {
                SmokeSendMessage(handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
                behind.Show(); Topmost = true; SmokeShowWindow(handle, 5); SmokeSetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); Activate(); await Task.Delay(500);
                var point = PointToScreen(new Point(15, 210));
                uint Sample()
                {
                    var dc = SmokeGetDC(IntPtr.Zero);
                    try { return SmokeGetPixel(dc, (int)point.X, (int)point.Y); }
                    finally { SmokeReleaseDC(IntPtr.Zero, dc); }
                }
                EnsureSmokeScreenVisible(handle, point);
                first = Sample();
                SaveComposedSmokeScreenshot(Path.Combine(directory, "backdrop-teal.png"));
                behind.Background = new SolidColorBrush(Color.FromRgb(220, 50, 170)); await Task.Delay(500);
                EnsureSmokeScreenVisible(handle, point);
                second = Sample();
                SaveComposedSmokeScreenshot(Path.Combine(directory, "backdrop-magenta.png"));
                difference = Enumerable.Range(0, 3).Sum(i => Math.Abs((int)(first >> (i * 8) & 255) - (int)(second >> (i * 8) & 255)));
                if (difference < 30) throw new InvalidOperationException($"Backdrop did not visibly transmit background colors: {first:X}/{second:X}");
                var drawing = new DrawingGroup();
                drawing.Children.Add(new GeometryDrawing(Brushes.Black, null, new RectangleGeometry(new Rect(0, 0, 6, 12))));
                drawing.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(6, 0, 6, 12))));
                behind.Background = new DrawingBrush(drawing) { Viewport = new Rect(0, 0, 12, 12), ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile };
                int StripeContrast()
                {
                    var a = PointToScreen(new Point(9, 210)); var b = PointToScreen(new Point(15, 210));
                    var dc = SmokeGetDC(IntPtr.Zero);
                    try
                    {
                        var ca = SmokeGetPixel(dc, (int)a.X, (int)a.Y); var cb = SmokeGetPixel(dc, (int)b.X, (int)b.Y);
                        return Enumerable.Range(0, 3).Sum(i => Math.Abs((int)(ca >> (i * 8) & 255) - (int)(cb >> (i * 8) & 255)));
                    }
                    finally { SmokeReleaseDC(IntPtr.Zero, dc); }
                }
                SmokeShowWindow(handle, 0);
                SmokeSetWindowPos(new WindowInteropHelper(behind).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); behind.Activate();
                await Task.Delay(350); EnsureSmokeScreenVisible(new WindowInteropHelper(behind).Handle, point); rawStripeContrast = StripeContrast();
                SmokeShowWindow(handle, 5); SmokeSetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); Activate(); await Task.Delay(500); EnsureSmokeScreenVisible(handle, point); blurredStripeContrast = StripeContrast();
                SaveComposedSmokeScreenshot(Path.Combine(directory, "backdrop-blur.png"));
                if (rawStripeContrast < 600 || blurredStripeContrast > rawStripeContrast / 10)
                    throw new InvalidOperationException($"Background pattern was not blurred: {rawStripeContrast}/{blurredStripeContrast}");
            }
            catch (SmokeScreenOccludedException) { screenOccluded = true; }
            finally { SmokeSendMessage(handle, 0x0232, IntPtr.Zero, IntPtr.Zero); behind.Close(); Topmost = wasTopmost; }
        }
        File.WriteAllText(Path.Combine(directory, "backdrop-report.json"), System.Text.Json.JsonSerializer.Serialize(new
        { LegacyBlur = legacyBackdrop, SystemBackdrop = backdropEnabled, BlurMaintainedDuringMoveAndResize = true, TintUnchanged = true,
            NativeMoveCount = 30, NativeResizeCount = 30, MoveMilliseconds = elapsed.ElapsedMilliseconds, BackgroundPixelA = first, BackgroundPixelB = second, BackgroundColorDifference = difference, RawStripeContrast = rawStripeContrast, BlurredStripeContrast = blurredStripeContrast, ScreenOccluded = screenOccluded }));
    }
    private sealed class SmokeScreenOccludedException : Exception;
    private static void EnsureSmokeScreenVisible(IntPtr handle, Point point)
    {
        var screenWindow = SmokeWindowFromPoint(new SmokePoint { X = (int)point.X, Y = (int)point.Y });
        if (SmokeGetAncestor(screenWindow, 2) != handle) throw new SmokeScreenOccludedException();
    }
    [StructLayout(LayoutKind.Sequential)] private struct SmokePoint { public int X; public int Y; }
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern IntPtr SmokeWindowFromPoint(SmokePoint point);
    [DllImport("user32.dll", EntryPoint = "GetAncestor")] private static extern IntPtr SmokeGetAncestor(IntPtr handle, int flags);
    private void SaveComposedSmokeScreenshot(string path)
    {
        var origin = PointToScreen(new Point()); var end = PointToScreen(new Point(ActualWidth, ActualHeight));
        var width = (int)(end.X - origin.X); var height = (int)(end.Y - origin.Y);
        var dc = SmokeGetDC(IntPtr.Zero); var memory = SmokeCreateCompatibleDC(dc); var bitmap = SmokeCreateCompatibleBitmap(dc, width, height);
        var previous = SmokeSelectObject(memory, bitmap);
        try
        {
            SmokeBitBlt(memory, 0, 0, width, height, dc, (int)origin.X, (int)origin.Y, 0x00CC0020);
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = File.Create(path); encoder.Save(output);
        }
        finally { SmokeSelectObject(memory, previous); SmokeDeleteObject(bitmap); SmokeDeleteDC(memory); SmokeReleaseDC(IntPtr.Zero, dc); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SmokeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool SmokeGetWindowRect(IntPtr handle, out SmokeRect rect);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool SmokeShowWindow(IntPtr handle, int command);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SmokeSendMessage(IntPtr handle, int message, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool SmokeSetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, int flags);
    [DllImport("user32.dll", EntryPoint = "GetDC")] private static extern IntPtr SmokeGetDC(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")] private static extern int SmokeReleaseDC(IntPtr handle, IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "GetPixel")] private static extern uint SmokeGetPixel(IntPtr dc, int x, int y);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC")] private static extern IntPtr SmokeCreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleBitmap")] private static extern IntPtr SmokeCreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll", EntryPoint = "SelectObject")] private static extern IntPtr SmokeSelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "BitBlt")] private static extern bool SmokeBitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int sx, int sy, uint op);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] private static extern bool SmokeDeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "DeleteDC")] private static extern bool SmokeDeleteDC(IntPtr dc);
}
