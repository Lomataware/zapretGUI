using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace ZapretGUI.Controls;

public partial class VerdictBadge : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(VerdictBadge), new PropertyMetadata(""));
    public static readonly DependencyProperty BackgroundKeyProperty =
        DependencyProperty.Register(nameof(BackgroundKey), typeof(string), typeof(VerdictBadge), new PropertyMetadata("CardBrush"));
    public static readonly DependencyProperty ForegroundKeyProperty =
        DependencyProperty.Register(nameof(ForegroundKey), typeof(string), typeof(VerdictBadge), new PropertyMetadata("TextPrimaryBrush"));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string BackgroundKey { get => (string)GetValue(BackgroundKeyProperty); set => SetValue(BackgroundKeyProperty, value); }
    public string ForegroundKey { get => (string)GetValue(ForegroundKeyProperty); set => SetValue(ForegroundKeyProperty, value); }
}

/// <summary>Преобразует имя кисти ("SuccessBrush") из текущего словаря темы в SolidColorBrush.</summary>
[ValueConversion(typeof(string), typeof(System.Windows.Media.Brush))]
public sealed class BrushByKeyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var key = value as string ?? "CardBrush";
        if (Application.Current != null && Application.Current.Resources[key] is System.Windows.Media.Brush b)
            return b;
        return System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>int == ConverterParameter → bool (двусторонний) — для RadioButton-навигации по int SelectedPage.</summary>
[ValueConversion(typeof(int), typeof(bool))]
public sealed class IntEqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is int i && int.TryParse(parameter?.ToString(), out var p) && i == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => (value is true && int.TryParse(parameter?.ToString(), out var p)) ? p : Binding.DoNothing;
}

/// <summary>int == ConverterParameter → Visibility.</summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is int i && int.TryParse(parameter?.ToString(), out var p) && i == p
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => Binding.DoNothing;
}
