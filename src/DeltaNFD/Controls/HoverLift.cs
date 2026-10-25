using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace DeltaNFD.Controls;

/// <summary>Presentation-only card motion. No timers, services, sampling or system writes.</summary>
public static class HoverLift
{
    private sealed class Motion(CompositeTransform transform)
    {
        internal CompositeTransform Transform = transform;
        internal double OriginalY = transform.TranslateY;
        internal Storyboard? Animation;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, Motion> Motions = new();
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(HoverLift), new PropertyMetadata(false, OnEnabledChanged));
    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        if (e.NewValue is true)
        {
            element.PointerEntered += Enter;
            element.PointerExited += Exit;
            element.PointerCanceled += Exit;
            element.Unloaded += Unload;
        }
        else
        {
            element.PointerEntered -= Enter;
            element.PointerExited -= Exit;
            element.PointerCanceled -= Exit;
            element.Unloaded -= Unload;
            Reset(element);
        }
    }
    private static void Enter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => Move(sender, -4);
    private static void Exit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => Move(sender, 0);
    private static void Unload(object sender, RoutedEventArgs e) { if (sender is FrameworkElement element) Reset(element); }

    private static void Move(object sender, double offset)
    {
        if (sender is not FrameworkElement element) return;
        var transform = MotionTransform(element);
        if (transform is null) return; // Preserve another owner's transform.
        var motion = Motions.GetValue(element, _ => new(transform));
        var currentY = transform.TranslateY;
        motion.Animation?.Stop();
        if (!ReferenceEquals(motion.Transform, transform))
        {
            motion.Transform = transform;
            motion.OriginalY = transform.TranslateY;
        }
        var destination = motion.OriginalY + offset;
        bool animate;
        try { animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { animate = false; }
        if (!animate) { transform.TranslateY = destination; return; }
        var animation = new DoubleAnimation
        {
            From = currentY,
            To = destination,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "TranslateY");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        motion.Animation = storyboard;
        storyboard.Begin();
    }
    internal static CompositeTransform? MotionTransform(FrameworkElement element)
    {
        if (element.RenderTransform is CompositeTransform composite) return composite;
        // WinUI returns an identity MatrixTransform even when RenderTransform has never been set.
        // Only this implicit default may be replaced; local/bound/styled transforms keep their owner.
        var implicitIdentity = element.RenderTransform is MatrixTransform matrix && matrix.Matrix.IsIdentity
            && ReferenceEquals(element.ReadLocalValue(UIElement.RenderTransformProperty), DependencyProperty.UnsetValue);
        for (var style = element.Style; implicitIdentity && style is not null; style = style.BasedOn)
            if (style.Setters.OfType<Setter>().Any(s => s.Property == UIElement.RenderTransformProperty)) implicitIdentity = false;
        if (element.RenderTransform is not null && !implicitIdentity) return null;
        var transform = new CompositeTransform();
        element.RenderTransform = transform;
        return transform;
    }
    private static void Reset(FrameworkElement element)
    {
        if (!Motions.TryGetValue(element, out var motion)) return;
        motion.Animation?.Stop();
        motion.Transform.TranslateY = motion.OriginalY;
        Motions.Remove(element);
    }
}
