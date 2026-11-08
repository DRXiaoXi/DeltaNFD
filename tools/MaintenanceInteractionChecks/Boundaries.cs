using System.ComponentModel;

namespace DeltaNFD.Services
{
    // Only app/OS boundaries are replaced; tests compile the actual AceViewModel.
    internal static class ServiceLocator
    {
        internal static IAceService Ace => throw new Exception("Tests must inject a read-only ACE fake");
        internal static ISystemTweakService SystemTweaks => throw new Exception("Unexpected system modification");
        internal static ProcessStub GameProcess { get; } = new();
    }
    internal interface IGameProcessService { bool IsGameRunning { get; } }
    internal sealed class ProcessStub : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    }
}
namespace DeltaNFD.Services.Backends
{
    internal static class AceCleanupReadiness
    {
        internal static bool RequiresReview => false;
        internal static string DescribeBlocker() => "";
    }
}
