using System.IO;
using System.Windows;

namespace DedupDesk.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!e.Args.Contains("--smoke"))
        {
            string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            instanceMutex = new Mutex(true, "Local\\DedupDesk-" + sid, out bool created);
            if (!created) { MessageBox.Show("DedupDesk 已在运行，请使用已有窗口。", "DedupDesk"); instanceMutex.Dispose(); instanceMutex = null; Shutdown(); return; }
        }
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "DedupDesk", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        var window = new MainWindow(); MainWindow = window;
        if (e.Args.Length >= 2 && e.Args[0] == "--smoke")
            window.Loaded += async (_, _) =>
            {
                try { await window.SmokeAsync(Path.GetFullPath(e.Args[1])); Shutdown(0); }
                catch (Exception ex) { File.WriteAllText(e.Args[1] + ".error.txt", ex.ToString()); Shutdown(1); }
            };
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { if (instanceMutex is not null) { instanceMutex.ReleaseMutex(); instanceMutex.Dispose(); } base.OnExit(e); }
}
