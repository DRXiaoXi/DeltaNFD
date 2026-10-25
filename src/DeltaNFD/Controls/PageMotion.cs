using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace DeltaNFD.Controls;

/// <summary>Navigation visuals only. Never cancels navigation, invokes commands, or opens page sessions.</summary>
public static class PageMotion
{
    private sealed class State(Frame frame)
    {
        internal TransitionCollection OriginalTransitions=frame.ContentTransitions;
        internal Storyboard? Running;
        internal readonly List<(FrameworkElement Element,double Opacity,CompositeTransform? Transform,double Y)> Targets=[];
        internal void Stop()
        {
            Running?.Stop();Running=null;
            foreach(var target in Targets){target.Element.Opacity=target.Opacity;if(target.Transform is not null)target.Transform.TranslateY=target.Y;}
            Targets.Clear();
        }
    }
    private static readonly ConditionalWeakTable<Frame,State> States=new();
    public static readonly DependencyProperty EnabledProperty=DependencyProperty.RegisterAttached("Enabled",typeof(bool),typeof(PageMotion),new PropertyMetadata(false,Changed));
    public static readonly DependencyProperty StaggerProperty=DependencyProperty.RegisterAttached("Stagger",typeof(bool),typeof(PageMotion),new PropertyMetadata(false));
    public static void SetEnabled(DependencyObject d,bool value)=>d.SetValue(EnabledProperty,value);
    public static bool GetEnabled(DependencyObject d)=>(bool)d.GetValue(EnabledProperty);
    public static void SetStagger(DependencyObject d,bool value)=>d.SetValue(StaggerProperty,value);
    public static bool GetStagger(DependencyObject d)=>(bool)d.GetValue(StaggerProperty);
    private static bool IsHome(Type? type)=>type?.FullName=="DeltaNFD.Views.DashboardPage";
    private static void Changed(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        if(d is not Frame frame)return;
        if(e.NewValue is true){States.GetValue(frame,f=>new(f));frame.Navigating+=Navigating;frame.Navigated+=Navigated;frame.Unloaded+=Unloaded;}
        else{frame.Navigating-=Navigating;frame.Navigated-=Navigated;frame.Unloaded-=Unloaded;if(States.TryGetValue(frame,out var s)){s.Stop();frame.ContentTransitions=s.OriginalTransitions;}States.Remove(frame);}
    }
    private static void Unloaded(object sender,RoutedEventArgs e){if(States.TryGetValue((Frame)sender,out var s))s.Stop();}
    private static void Navigating(object sender,NavigatingCancelEventArgs e)
    {
        var frame=(Frame)sender;var state=States.GetValue(frame,f=>new(f));state.Stop();
        frame.ContentTransitions=IsHome(e.SourcePageType)?state.OriginalTransitions:new TransitionCollection{new NavigationThemeTransition{DefaultNavigationTransitionInfo=new SuppressNavigationTransitionInfo()}};
    }
    private static void Navigated(object sender,NavigationEventArgs e)
    {
        if(IsHome(e.SourcePageType)||e.Content is not FrameworkElement content)return;
        var frame=(Frame)sender;var state=States.GetValue(frame,f=>new(f));state.Stop();
        try{Animate(state,content);}catch{state.Stop();} // A visual failure must not interrupt page functionality.
    }
    private static void Animate(State state,FrameworkElement content)
    {
        try{if(!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)return;}catch{return;}
        var storyboard=new Storyboard();
        var panels=GetStagger(content)?FindStaggerPanel(content):null;
        var targets=panels is null?new[]{content}:panels.Children.OfType<FrameworkElement>().ToArray();
        for(var index=0;index<targets.Length;index++)
        {
            var element=targets[index];if(element.Visibility!=Visibility.Visible)continue;
            var opacity=element.Opacity;CompositeTransform? transform=null;var y=0d;
            // Staggered cards use dedicated wrappers, so HoverLift owns the inner card transform.
            if(HoverLift.MotionTransform(element) is CompositeTransform composite){transform=composite;y=composite.TranslateY;}
            state.Targets.Add((element,opacity,transform,y));
            var delay=TimeSpan.FromMilliseconds(panels is null?0:Math.Min(index,8)*40);
            var duration=new Duration(TimeSpan.FromMilliseconds(panels is null?260:240));
            var fade=new DoubleAnimation{From=0,To=opacity,BeginTime=delay,Duration=duration};
            Storyboard.SetTarget(fade,element);Storyboard.SetTargetProperty(fade,"Opacity");storyboard.Children.Add(fade);
            if(transform is not null)
            {
                var slide=new DoubleAnimation{From=y+(panels is null?-24:12),To=y,BeginTime=delay,Duration=duration,EnableDependentAnimation=true,EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}};
                Storyboard.SetTarget(slide,transform);Storyboard.SetTargetProperty(slide,"TranslateY");storyboard.Children.Add(slide);
            }
        }
        state.Running=storyboard;
        storyboard.Completed+=(_,_)=>{if(ReferenceEquals(state.Running,storyboard))state.Stop();};
        storyboard.Begin();
    }
    private static Panel? FindStaggerPanel(DependencyObject root)
    {
        if(root is Panel panel&&GetStagger(panel))return panel;
        if(root is ContentControl control&&control.Content is DependencyObject childContent)
        {
            var logical=FindStaggerPanel(childContent);if(logical is not null)return logical;
        }
        var count=VisualTreeHelper.GetChildrenCount(root);
        for(var i=0;i<count;i++){var found=FindStaggerPanel(VisualTreeHelper.GetChild(root,i));if(found is not null)return found;}
        return null;
    }
}
