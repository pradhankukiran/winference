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
}
