namespace DeltaNFD.Services;

// The read-only smoke executable does not compile the WinUI service locator.
internal static class ServiceLocator
{
    internal static IPowerService Power { get; } = new PowerService();
}
