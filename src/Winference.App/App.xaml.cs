using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Winference.App.Diagnostics;

namespace Winference.App;

public partial class App : Application
{
    private MainWindow? _window;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;

    public App()
    {
        UnhandledException += HandleUnhandledException;
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        DebugSettings.XamlResourceReferenceFailed += (_, args) =>
            AppDiagnostics.Write("xaml.resource.missing", new { args.Message });
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("application.resources.failed", exception);
            ShowStartupFailure();
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _instanceMutex = new Mutex(true, @"Local\Winference.Shell" + Services.TestHardware.InstanceSuffix(), out _ownsMutex);
            if (!_ownsMutex)
            {
                BringExistingWindowForward();
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Exit();
                return;
            }

            var logsAvailable = AppDiagnostics.Write("application.start", new
            {
                version = typeof(App).Assembly.GetName().Version?.ToString(),
                windows = Environment.OSVersion.Version.ToString(),
                architecture = RuntimeInformation.ProcessArchitecture.ToString()
            });
            _window = new MainWindow();
            if (!logsAvailable)
                _window.ViewModel.ReportShellError("Application logs could not be written. Check your folder permissions.");
            _window.Closed += (_, _) =>
            {
                AppDiagnostics.Write("application.close");
                if (_ownsMutex) _instanceMutex?.ReleaseMutex();
                _ownsMutex = false;
                _instanceMutex?.Dispose();
                _instanceMutex = null;
            };
            _window.Activate();
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("application.start.failed", exception);
            ShowStartupFailure();
            Exit();
        }
    }

    public static void OpenLogsFolder() => OpenFolder(AppDiagnostics.LogsDirectory);

    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppDiagnostics.WriteException("diagnostics.open.failed", exception);
            if (Current is App app && app._window is not null)
                app._window.ViewModel.ReportShellError("The folder could not be opened. Check your folder permissions and try again.");
        }
    }

    private void HandleUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        AppDiagnostics.WriteException("application.unhandled", args.Exception);
        // Keep the original failure semantics: unknown UI failures must not be
        // swallowed while the application continues with corrupted state.
    }

    private static void BringExistingWindowForward()
    {
        using var current = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(current.ProcessName))
        {
            using (process)
            {
                if (process.Id == current.Id || process.SessionId != current.SessionId) continue;
                try
                {
                    if (process.MainModule?.FileName != current.MainModule?.FileName) continue;
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) continue;
                    ShowWindow(handle, 9); // SW_RESTORE
                    SetForegroundWindow(handle);
                    return;
                }
                catch (System.ComponentModel.Win32Exception) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static void ShowStartupFailure()
    {
        MessageBox(IntPtr.Zero,
            "Winference could not open. Try opening it again. Startup details are saved in your Winference app logs when available.",
            "Winference", 0x10);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr window, string message, string title, uint type);
}
