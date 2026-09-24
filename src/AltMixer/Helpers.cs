using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using AltMixer.Core.Model;
using AltMixer.ViewModels;

namespace AltMixer;

public sealed class SettingTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Level { get; set; }
    public DataTemplate? Toggle { get; set; }
    public DataTemplate? Choice { get; set; }
    public DataTemplate? Opaque { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => (item as SettingViewModel)?.Kind switch
    {
        SettingKind.Level => Level,
        SettingKind.Toggle => Toggle,
        SettingKind.Choice => Choice,
        SettingKind.Opaque => Opaque,
        _ => null,
    };
}

public sealed class Not : IValueConverter
{
    public static readonly Not Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

public sealed class NotNullVisible : IValueConverter
{
    public static readonly NotNullVisible Instance = new();
    public object Convert(object? value, Type t, object p, CultureInfo c) => value == null ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class Positive : IValueConverter
{
    public static readonly Positive Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int i && i > 0;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Mouse wheel over a slider nudges it by SmallChange (instead of scrolling the page).</summary>
public static class SliderWheel
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SliderWheel), new PropertyMetadata(false, OnChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase r) return;
        if ((bool)e.NewValue) r.PreviewMouseWheel += Wheel; else r.PreviewMouseWheel -= Wheel;
    }

    static void Wheel(object sender, MouseWheelEventArgs e)
    {
        var r = (RangeBase)sender;
        if (!r.IsEnabled) return;
        r.Value = Math.Clamp(r.Value + Math.Sign(e.Delta) * r.SmallChange, r.Minimum, r.Maximum);
        e.Handled = true;
    }
}
