using System.IO;
using System.Windows;
using System.Windows.Threading;
using FftTrainer.Core;

namespace FftTrainer.App;

public partial class App : Application
{
    // 崩溃日志：%LOCALAPPDATA%\FftTrainer\crash.log
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        MessageBox.Show(Localizer.F("ui.crash.message", CrashLog, e.Exception.Message), Localizer.T("app.name"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void Log(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // 写日志失败不能再抛
        }
    }
}
