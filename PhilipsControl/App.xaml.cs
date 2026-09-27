using System.Windows;
using System.Windows.Threading;

namespace PhilipsControl;

public partial class App : Application
{
    private const string InstanceName = "PhilipsControl.SingleInstance.9C3E";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // A second launch (e.g. from the Start menu while the app sits in the tray) just brings the running window forward.
        _instanceMutex = new Mutex(true, InstanceName, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow(startMinimized);
        MainWindow = window;
        if (!startMinimized) window.Show();

        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(window.BringToFront), null, Timeout.Infinite, false);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the remote alive: a transient network or parsing error should never take the whole app down.
        e.Handled = true;
        (MainWindow as MainWindow)?.ReportUnhandled(e.Exception);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSignal?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }
}
