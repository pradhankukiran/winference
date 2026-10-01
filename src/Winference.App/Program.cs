using System.Runtime.CompilerServices;
using Winference.Infrastructure.Hybrid;

namespace Winference.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--hybrid-worker")
            return HybridWorker.RunAsync(args).GetAwaiter().GetResult();
        StartInterface();
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartInterface()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(parameters =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }
}
