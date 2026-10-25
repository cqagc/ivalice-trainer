using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FftTrainer.Core;

namespace FftTrainer.App;

public partial class App : Application
{
    // 崩溃日志：%LOCALAPPDATA%\FftTrainer\crash.log
    private static readonly string CrashLog = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FftTrainer", "crash.log");

    private SingleInstance? _singleInstance;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);
        try
        {
            _singleInstance = new SingleInstance();
            if (!_singleInstance.EnterOrActivate())
            {
                Shutdown();
                return;
            }

            _singleInstance.StartListening(ActivateExistingWindowAsync, Log);
            base.OnStartup(e);
            // No StartupUri: nothing can construct GameSession before we own the mutex.
            MainWindow = new MainWindow();
            MainWindow.Closed += (_, _) => _exiting = true;
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            Log(ex);
            // Startup/IPC failure is fail-closed; use the existing localized error presentation.
            MessageBox.Show(Localizer.F("ui.crash.message", CrashLog, ex.Message), Localizer.T("app.name"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }

    private async Task<bool> ActivateExistingWindowAsync(CancellationToken cancellationToken)
    {
        if (Dispatcher.HasShutdownStarted)
            return false;
        return await Dispatcher.InvokeAsync(() =>
        {
            if (_exiting || MainWindow is not { IsLoaded: true } window)
                return false;
            var handle = new WindowInteropHelper(window).Handle;
            if (IsIconic(handle))
                ShowWindow(handle, 9); // SW_RESTORE also preserves a previously maximized window.
            window.Activate();
            SetForegroundWindow(handle); // Best effort; Windows foreground policy still applies.
            return true;
        }, DispatcherPriority.Normal, cancellationToken).Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        try
        {
            // OnExit runs on the same UI thread that acquired the Windows mutex.
            _singleInstance?.Dispose();
        }
        finally
        {
            base.OnExit(e);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
