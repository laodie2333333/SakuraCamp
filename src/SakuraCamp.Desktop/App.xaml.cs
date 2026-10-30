using System.IO;
using System.Windows;

namespace SakuraCamp.Desktop;

public partial class App : Application
{
    public static string DataRoot { get; private set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SakuraCamp");
    public static string? SmokeDirectory { get; private set; }
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-output")
        {
            SmokeDirectory = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(SmokeDirectory);
            DataRoot = Path.Combine(SmokeDirectory, "app-data");
        }
        else
        {
            instance = new Mutex(true, @"Local\SakuraCamp.Desktop", out var created);
            if (!created) { MessageBox.Show("樱火营地已经在运行，请切换到现有窗口。"); Shutdown(); return; }
        }
        MainWindow = new MainWindow(); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
