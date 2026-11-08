using System.Numerics;
using System.Reflection;
using DeltaNFD.Controls;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

internal static class Program
{
    private static int count;
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ => new ChecksApplication());
        return Environment.ExitCode;
    }

    private sealed class ChecksApplication : Application
    {
        internal ChecksApplication()
        {
            UnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); e.Handled = true; Environment.ExitCode = 1; Exit(); };
            DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
            {
                try { Run(); Console.WriteLine($"TOTAL {count} native WinUI motion checks passed; no Window created/activated, no input or real app services."); }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { Exit(); }
            });
        }
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        count++; Console.WriteLine("PASS: " + message);
    }
    private static void LegacyCall(string method, FrameworkElement element, params object[] rest)
    {
        var args = new object[] { element }.Concat(rest).ToArray();
        typeof(LegacyHoverLift).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    }
    private static void BaselineFailure(string method, params object[] rest)
    {
        var element = new Grid { RenderTransform = new CompositeTransform { TranslateY = 8 } };
        Exception? caught = null;
        try { LegacyCall(method, element, rest); }
        catch (TargetInvocationException ex) { caught = ex.InnerException; }
        Check(caught is UnauthorizedAccessException && caught.HResult == unchecked((int)0x80070005),
            "original production " + method + " reproduces native RenderTransform conflict, HRESULT 80070005");
    }
    private static Vector3 Translation(FrameworkElement element)
    {
        var result = ElementCompositionPreview.GetElementVisual(element).Properties.TryGetVector3("Translation", out var value);
        Check(result == CompositionGetValueStatus.Succeeded, "owned composition translation channel exists");
        return value;
    }
    private static void Run()
    {
        BaselineFailure("Move", -3f);
        BaselineFailure("Reset");

        var transform = new CompositeTransform { TranslateX = 11, TranslateY = 8, ScaleX = 1.25, Rotation = 3 };
        var tile = new Grid { RenderTransform = transform };
        HoverLift.SetEnabled(tile, true);
        Check(HoverLift.TryMove(tile, -3, true), "hover accepts the exact existing page RenderTransform case");
        Check(ReferenceEquals(tile.RenderTransform, transform) && transform.TranslateX == 11 && transform.TranslateY == 8
            && transform.ScaleX == 1.25 && transform.Rotation == 3, "page transform values and ownership remain intact");
        Check(HoverLift.TryMove(tile, 0, true), "pointer exit accepts the same RenderTransform");
        HoverLift.Reset(tile);
        Check(Translation(tile) == Vector3.Zero, "reset stops owned animation and returns to original composition position");
        for (var i = 0; i < 25; i++)
            Check(HoverLift.TryMove(tile, -3, true) && HoverLift.TryMove(tile, 0, true), "rapid enter/exit is safe " + i);
        HoverLift.SetEnabled(tile, false);
        Check(Translation(tile) == Vector3.Zero && ReferenceEquals(tile.RenderTransform, transform), "disabling restores only owned translation");
        HoverLift.SetEnabled(tile, true);
        Check(HoverLift.TryMove(tile, -3, true), "re-enabled behavior can reuse its own channel");
        Check(HoverLift.TryMove(tile, -3, false) && Translation(tile) == Vector3.Zero, "reduced motion restores position without lifting");

        var fresh = new Grid { RenderTransform = new CompositeTransform { TranslateY = 9 } };
        HoverLift.Reset(fresh);
        Check(ElementCompositionPreview.GetElementVisual(fresh).Properties.TryGetVector3("Translation", out _) != CompositionGetValueStatus.Succeeded,
            "unload before first hover creates no translation channel and cannot hit the old reset failure");
        Check(HoverLift.TryMove(fresh, -3, false), "disabled animations before first hover remain safe");

        var reversed = new Border();
        Check(HoverLift.TryMove(reversed, -3, true), "hover can initialize before page motion");
        Check(HoverLift.MotionTransform(reversed) is CompositeTransform, "page motion can subsequently acquire RenderTransform");
        Check(HoverLift.TryMove(reversed, 0, true), "hover still works after page transform was acquired");
        HoverLift.Reset(reversed);

        var style = new Style(typeof(StackPanel));
        style.Setters.Add(new Setter(UIElement.RenderTransformProperty, new CompositeTransform { ScaleY = 0.9 }));
        var styled = new StackPanel { Style = style };
        var styledTransform = styled.RenderTransform;
        Check(HoverLift.TryMove(styled, -3, true) && ReferenceEquals(styled.RenderTransform, styledTransform), "styled RenderTransform is preserved");
        HoverLift.Reset(styled);

        var other = new Grid();
        ElementCompositionPreview.SetIsTranslationEnabled(other, true);
        var otherVisual = ElementCompositionPreview.GetElementVisual(other);
        var otherPosition = new Vector3(4, 5, 6);
        otherVisual.Properties.InsertVector3("Translation", otherPosition);
        Check(!HoverLift.TryMove(other, -3, true), "another behavior's translation channel is not adopted");
        HoverLift.Reset(other);
        Check(Translation(other) == otherPosition, "reset never clears another behavior's position");

        var facade = new Grid { Scale = new Vector3(1.1f, 1.2f, 1) };
        var warnings = DeltaNFD.Services.Log.Warnings;
        Check(!HoverLift.TryMove(facade, -3, true), "an incompatible external facade is contained without throwing");
        Check(!HoverLift.TryMove(facade, -3, true) && DeltaNFD.Services.Log.Warnings == warnings + 1, "incompatible behavior is disabled and logged only once");
        HoverLift.Reset(facade);
        Check(facade.Scale == new Vector3(1.1f, 1.2f, 1), "external facade values remain unchanged");

        var eventTile = new Grid { RenderTransform = new CompositeTransform() };
        HoverLift.SetEnabled(eventTile, true);
        foreach (var method in new[] { "Enter", "Exit", "Unload" })
        {
            typeof(HoverLift).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [eventTile, null]);
            Check(true, "actual event boundary returns safely: " + method);
        }
        HoverLift.SetEnabled(eventTile, false);
    }
}
