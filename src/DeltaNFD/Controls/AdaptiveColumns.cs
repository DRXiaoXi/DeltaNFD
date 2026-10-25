using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaNFD.Controls;

/// <summary>Presentation only: collapse a two-column card layout to reading order at narrow widths.</summary>
public static class AdaptiveColumns
{
    private sealed class State { internal bool? Wide; internal int Count; internal bool Updating; }
    private static readonly ConditionalWeakTable<Grid, State> States = new();
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(AdaptiveColumns), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty ThresholdProperty = DependencyProperty.RegisterAttached("Threshold", typeof(double), typeof(AdaptiveColumns), new PropertyMetadata(900d));
    public static readonly DependencyProperty LeftWeightProperty = DependencyProperty.RegisterAttached("LeftWeight", typeof(double), typeof(AdaptiveColumns), new PropertyMetadata(3d));
    public static readonly DependencyProperty RightWeightProperty = DependencyProperty.RegisterAttached("RightWeight", typeof(double), typeof(AdaptiveColumns), new PropertyMetadata(2d));
    public static readonly DependencyProperty WideRowProperty = DependencyProperty.RegisterAttached("WideRow", typeof(int), typeof(AdaptiveColumns), new PropertyMetadata(0));
    public static readonly DependencyProperty WideColumnProperty = DependencyProperty.RegisterAttached("WideColumn", typeof(int), typeof(AdaptiveColumns), new PropertyMetadata(0));
    public static readonly DependencyProperty WideSpanProperty = DependencyProperty.RegisterAttached("WideSpan", typeof(int), typeof(AdaptiveColumns), new PropertyMetadata(1));
    public static void SetEnabled(DependencyObject d,bool v)=>d.SetValue(EnabledProperty,v);
    public static bool GetEnabled(DependencyObject d)=>(bool)d.GetValue(EnabledProperty);
    public static void SetThreshold(DependencyObject d,double v)=>d.SetValue(ThresholdProperty,v);
    public static double GetThreshold(DependencyObject d)=>(double)d.GetValue(ThresholdProperty);
    public static void SetLeftWeight(DependencyObject d,double v)=>d.SetValue(LeftWeightProperty,v);
    public static double GetLeftWeight(DependencyObject d)=>(double)d.GetValue(LeftWeightProperty);
    public static void SetRightWeight(DependencyObject d,double v)=>d.SetValue(RightWeightProperty,v);
    public static double GetRightWeight(DependencyObject d)=>(double)d.GetValue(RightWeightProperty);
    public static void SetWideRow(DependencyObject d,int v)=>d.SetValue(WideRowProperty,v);
    public static int GetWideRow(DependencyObject d)=>(int)d.GetValue(WideRowProperty);
    public static void SetWideColumn(DependencyObject d,int v)=>d.SetValue(WideColumnProperty,v);
    public static int GetWideColumn(DependencyObject d)=>(int)d.GetValue(WideColumnProperty);
    public static void SetWideSpan(DependencyObject d,int v)=>d.SetValue(WideSpanProperty,v);
    public static int GetWideSpan(DependencyObject d)=>(int)d.GetValue(WideSpanProperty);
    private static void Changed(DependencyObject d,DependencyPropertyChangedEventArgs e)
    {
        if(d is not Grid grid)return;
        if(e.NewValue is true){grid.Loaded+=Loaded;grid.SizeChanged+=Resized;}
        else{grid.Loaded-=Loaded;grid.SizeChanged-=Resized;States.Remove(grid);}
    }
    private static void Loaded(object sender,RoutedEventArgs e)=>Update((Grid)sender);
    private static void Resized(object sender,SizeChangedEventArgs e)=>Update((Grid)sender);
    private static void Update(Grid grid)
    {
        if(!grid.IsLoaded||grid.ActualWidth<=0)return;
        var state=States.GetValue(grid,_=>new());
        var wide=grid.ActualWidth>=Math.Max(300,GetThreshold(grid));
        if(state.Updating||state.Wide==wide&&state.Count==grid.Children.Count)return;
        state.Updating=true;
        try
        {
            state.Wide=wide;state.Count=grid.Children.Count;
            grid.ColumnDefinitions.Clear();
            grid.ColumnDefinitions.Add(new(){Width=new(wide?Math.Max(1,GetLeftWeight(grid)):1,GridUnitType.Star)});
            grid.ColumnDefinitions.Add(new(){Width=wide?new(Math.Max(1,GetRightWeight(grid)),GridUnitType.Star):new(0)});
            grid.ColumnSpacing=wide?12:0;
            var rows=wide?grid.Children.Select(GetWideRow).DefaultIfEmpty(0).Max()+1:grid.Children.Count;
            grid.RowDefinitions.Clear();for(var i=0;i<rows;i++)grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
            for(var i=0;i<grid.Children.Count;i++)
            {
                if(grid.Children[i] is not FrameworkElement child)continue;
                Grid.SetRow(child,wide?Math.Max(0,GetWideRow(child)):i);
                Grid.SetColumn(child,wide?Math.Clamp(GetWideColumn(child),0,1):0);
                Grid.SetColumnSpan(child,wide?Math.Clamp(GetWideSpan(child),1,2):1);
            }
        }
        finally{state.Updating=false;}
    }
}
