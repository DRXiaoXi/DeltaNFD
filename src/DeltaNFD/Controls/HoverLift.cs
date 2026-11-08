using System.Numerics;
using System.Runtime.CompilerServices;
using DeltaNFD.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace DeltaNFD.Controls;

/// <summary>
/// Presentation-only card motion. No timers, services, sampling or system writes.
/// 使用 ElementCompositionPreview 的 Translation 属性集，由合成线程执行。
/// 与页面入场的 RenderTransform 共存；不混用互斥的 UIElement 渲染属性。
/// </summary>
public static class HoverLift
{
    private sealed class MotionState
    {
        internal Visual? Visual;
        internal Vector3 Original;
        internal bool Disabled;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, MotionState> States = new();
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
    private static void Enter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => Move(sender, -3);
    private static void Exit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => Move(sender, 0);
    private static void Unload(object sender, RoutedEventArgs e) { if (sender is FrameworkElement element) Reset(element); }

    private static void Move(object sender, float offset)
    {
        if (sender is not FrameworkElement element) return;
        bool animate;
        try { animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { animate = false; }
        TryMove(element, offset, animate);
    }

    internal static bool TryMove(FrameworkElement element, float offset, bool animationsEnabled)
    {
        if (!animationsEnabled) { Reset(element); return true; }
        var state = States.GetValue(element, _ => new MotionState());
        if (state.Disabled) return false;
        try
        {
            if (state.Visual is null)
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                // A pre-existing translation channel belongs to another behavior.
                if (visual.Properties.TryGetVector3("Translation", out _) == CompositionGetValueStatus.Succeeded)
                { state.Disabled = true; return false; }
                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                state.Visual = visual;
                if (visual.Properties.TryGetVector3("Translation", out var original) == CompositionGetValueStatus.Succeeded)
                    state.Original = original;
            }
            var animation = state.Visual.Compositor.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(160);
            animation.InsertKeyFrame(1, state.Original + new Vector3(0, offset, 0));
            state.Visual.Properties.StartAnimation("Translation", animation);
            return true;
        }
        catch (Exception ex)
        {
            state.Disabled = true;
            Restore(state);
            // Cosmetic failures stay local; business exceptions keep the app's normal handling.
            Log.Warn("悬停动效已停用：" + ex.Message);
            return false;
        }
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
    internal static void Reset(FrameworkElement element)
    {
        // Exiting/unloading an element that never hovered must not claim a rendering API.
        if (States.TryGetValue(element, out var state)) Restore(state);
    }

    private static void Restore(MotionState state)
    {
        if (state.Visual is null) return;
        try
        {
            state.Visual.Properties.StopAnimation("Translation");
            state.Visual.Properties.InsertVector3("Translation", state.Original);
        }
        catch (Exception ex)
        {
            if (!state.Disabled) Log.Warn("悬停动效复位未完成：" + ex.Message);
            state.Disabled = true;
        }
    }
}
