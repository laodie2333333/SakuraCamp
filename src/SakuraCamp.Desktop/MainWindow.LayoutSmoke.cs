using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private static byte[] RenderInput(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private async Task VerifyConnectionLayoutSmokeAsync(string directory)
    {
        var oldWidth = Width; var oldHeight = Height;
        var oldSecret = SakuraToken.Password; var oldToken = KleiToken.Password; var oldPassword = WorldPassword.Password;
        var oldNode = CreationNode.SelectedItem;
        Tabs.SelectedItem = ConnectionTab; UpdateLayout();
        var scroll = FindVisualParent<ScrollViewer>(ConnectionFields) ?? throw new InvalidOperationException("Connection scroll container missing");
        var measurements = new List<object>();
        try
        {
            Tabs.SelectedItem = ConnectionTab;
            foreach (var size in new[] { (1220d, 850d), (MinWidth, MinHeight) })
            {
                Width = size.Item1; Height = size.Item2;
                scroll.ScrollToBottom(); await Task.Delay(150); UpdateLayout();
                SakuraToken.Clear(); KleiToken.Clear(); WorldPassword.Clear(); UpdateLayout(); await Task.Delay(60);
                var emptyPixels = new[] { RenderInput(SakuraToken), RenderInput(KleiToken), RenderInput(WorldPassword) };
                var controls = new FrameworkElement[] { SakuraToken, CreationNode, KleiToken, WorldPassword };
                (double Width, double X)[] Bounds() => controls.Select(c => (c.ActualWidth, c.TranslatePoint(new Point(), ConnectionFields).X)).ToArray();
                var original = Bounds();
                foreach (var length in new[] { 12, 128, 4096 })
                {
                    SakuraToken.Password = new string('x', length); KleiToken.Password = new string('k', length); WorldPassword.Password = new string('p', length);
                    await Task.Delay(60); UpdateLayout();
                    var current = Bounds();
                    for (var index = 0; index < original.Length; index++)
                        if (Math.Abs(original[index].Width - current[index].Width) > 1 || Math.Abs(original[index].X - current[index].X) > 1)
                            throw new InvalidOperationException($"Typing {length} characters shifted or resized input {index}");
                    var inputs = new[] { SakuraToken, KleiToken, WorldPassword };
                    var drawn = new List<int>();
                    for (var index = 0; index < inputs.Length; index++)
                    {
                        var input = inputs[index]; var filled = RenderInput(input); var empty = emptyPixels[index];
                        var width = (int)input.ActualWidth; var height = (int)input.ActualHeight; var changed = 0;
                        // Check the interior, excluding borders and caret. Masked text must have visible ink.
                        for (var y = 8; y < height - 8; y++) for (var x = 16; x < width - 16; x++)
                        {
                            var offset = (y * width + x) * 4;
                            if (Math.Abs(filled[offset] - empty[offset]) + Math.Abs(filled[offset + 1] - empty[offset + 1]) + Math.Abs(filled[offset + 2] - empty[offset + 2]) > 80) changed++;
                        }
                        if (changed < 15)
                        {
                            SaveSmokeScreenshot(Path.Combine(directory, "input-failure.png"));
                            File.WriteAllLines(Path.Combine(directory, "input-visuals.txt"), VisualChildren<FrameworkElement>(input).Select(c => $"{c.GetType().Name} {c.Name} Actual={c.ActualWidth}x{c.ActualHeight} Desired={c.DesiredSize} Margin={c.Margin} Visible={c.IsVisible}"));
                            throw new InvalidOperationException($"Password input {index} accepted text but rendered no visible mask");
                        }
                        drawn.Add(changed);
                    }
                    measurements.Add(new { WindowWidth = size.Item1, Characters = length, Widths = current.Select(c => c.Width), MaskedPixels = drawn });
                }
                var originalItems = CreationNode.ItemsSource;
                try
                {
                    CreationNode.ItemsSource = new[] { new NodeInfo(999, new string('N', 512), new string('h', 512) + ".example.com", 0, 4 | 32) };
                    CreationNode.SelectedIndex = 0; await Task.Delay(50); UpdateLayout();
                    var afterNode = Bounds();
                    for (var index = 0; index < original.Length; index++)
                        if (Math.Abs(original[index].Width - afterNode[index].Width) > 1 || Math.Abs(original[index].X - afterNode[index].X) > 1)
                            throw new InvalidOperationException("Long node label displaced form inputs");
                }
                finally { CreationNode.ItemsSource = originalItems; CreationNode.SelectedItem = oldNode; }
                SakuraToken.Password = "demo-access-key"; KleiToken.Password = "pds-demo-token-fixture-only"; WorldPassword.Password = "test-password";
                UpdateLayout(); SaveSmokeScreenshot(Path.Combine(directory, size.Item1 == MinWidth ? "minimum-input-filled.png" : "input-filled.png"));
                var screenshot = Path.Combine(directory, size.Item1 == MinWidth ? "minimum-input-fields.png" : "input-fields.png");
                var bitmap = new RenderTargetBitmap((int)ConnectionFields.ActualWidth, (int)ConnectionFields.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(ConnectionFields); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(screenshot); encoder.Save(stream);
            }
            Tabs.SelectedItem = OverviewTab; UpdateLayout();
            var cards = VisualChildren<ListBoxItem>(WorldList).ToArray();
            if (cards.Length < 2 || !VisualChildren<WrapPanel>(WorldList).Any()) throw new InvalidOperationException("Worlds are not displayed as wrapping cards");
            var first = cards[0].TranslatePoint(new Point(), WorldList); var second = cards[1].TranslatePoint(new Point(), WorldList);
            if (Math.Abs(first.Y - second.Y) > 1 || second.X <= first.X) throw new InvalidOperationException("World cards do not wrap into columns");
            var selected = cards.First(c => c.IsSelected);
            if (selected.Template.FindName("SelectedBadge", selected) is not Border { Visibility: Visibility.Visible }) throw new InvalidOperationException("Selected world card has no badge");
            var originalWorld = CurrentWorld;
            var another = cards.First(c => !c.IsSelected);
            WorldList.SelectedItem = another.DataContext; await modRefreshTask; UpdateLayout();
            if (CurrentWorld != (WorldInfo)another.DataContext || WorldTitle.Text != CurrentWorld?.Name) throw new InvalidOperationException("Selecting world card did not update connection context");
            WorldList.SelectedItem = originalWorld; await modRefreshTask;
            File.WriteAllText(Path.Combine(directory, "input-layout-report.json"), System.Text.Json.JsonSerializer.Serialize(measurements));
        }
        finally
        {
            Width = oldWidth; Height = oldHeight;
            SakuraToken.Password = oldSecret; KleiToken.Password = oldToken; WorldPassword.Password = oldPassword; CreationNode.SelectedItem = oldNode;
            scroll.ScrollToTop();
        }
    }
}
