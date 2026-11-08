namespace DeltaNFD.Services;

// This fixture writes diagnostics to the inherited pipe, never to real app data.
internal static class Log
{
    internal static int Warnings;
    public static void Warn(string message) { Warnings++; Console.WriteLine("DIAGNOSTIC: " + message); }
}
