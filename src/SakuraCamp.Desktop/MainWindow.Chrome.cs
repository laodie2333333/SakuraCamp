using System.Windows;
using System.Windows.Automation;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace SakuraCamp.Desktop;

public partial class MainWindow
{
    private void InitializeWindowChrome()
    {
        StateChanged += (_, _) => UpdateWindowChrome();
        SourceInitialized += (_, _) => EnableWindowBackdrop();
        UpdateWindowChrome();
    }
    private bool backdropEnabled;
    private bool legacyBackdrop;
    private void EnableWindowBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var chromeSource = HwndSource.FromHwnd(handle);
        if (!SystemParameters.HighContrast)
        {
            var dark = 1; _ = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            // Only request the documented system backdrop on Windows 11 22H2+.
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                var backdrop = 3;
                backdropEnabled = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int)) == 0;
            }
            // Windows 10 acrylic can stall window dragging. Use native blur instead,
            // and leave it enabled throughout moving and resizing.
            if (!backdropEnabled) backdropEnabled = legacyBackdrop = SetAccent(handle, 3);
        }
        if (backdropEnabled && chromeSource is { } source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            RestoreBackdropTint();
        }
        if (!backdropEnabled) WindowSurface.Background = new SolidColorBrush(Color.FromRgb(30, 35, 48));
    }
    private void RestoreBackdropTint() => WindowSurface.Background = new SolidColorBrush(Color.FromArgb(legacyBackdrop ? (byte)0x99 : (byte)0x20, 30, 35, 48));
    private static bool SetAccent(IntPtr handle, int state)
    {
        // ABGR tint: let the desktop through instead of stacking two opaque dark layers.
        var policy = new AccentPolicy { State = state, Flags = 2, Color = 0x9930231E };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, pointer, false);
            var data = new AccentData { Attribute = 19, Data = pointer, Size = Marshal.SizeOf<AccentPolicy>() };
            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int State; public int Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentData { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr window, ref AccentData data);
    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowSurface.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeGlyph.Text = maximized ? "\uE923" : "\uE922";
        AutomationProperties.SetName(MaximizeCaption, maximized ? "还原" : "最大化");
    }
    private void MinimizeCaption_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void MaximizeCaption_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }
    private void CloseCaption_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
