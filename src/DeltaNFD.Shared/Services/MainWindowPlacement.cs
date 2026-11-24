namespace DeltaNFD.Services;

/// <summary>The main window's restored rectangle in screen pixels and its last visible mode.</summary>
public sealed record MainWindowPlacement(int X, int Y, int Width, int Height, bool Maximized);
