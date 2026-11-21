using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeltaNFD.Controls;

/// <summary>When closed, let the containing page scroll without changing the selected setting.</summary>
public sealed class PageScrollComboBox : ComboBox
{
    protected override void OnPointerWheelChanged(PointerRoutedEventArgs e)
    {
        if (IsDropDownOpen)
            base.OnPointerWheelChanged(e);
    }
}
