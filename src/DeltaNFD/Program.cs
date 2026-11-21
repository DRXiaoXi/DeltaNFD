using System.Runtime.CompilerServices;
using DeltaNFD.Native;

namespace DeltaNFD;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Keep the WinUI entry in a different JIT method: native residency never references XAML.
        if (LightweightTray.ShouldLaunchUi(args)) StartWinUi();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartWinUi()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(dispatcher));
            _ = new App();
        });
    }
}
