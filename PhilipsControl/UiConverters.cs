using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PhilipsControl.Models;

namespace PhilipsControl;

/// <summary>true → Visible. Pass ConverterParameter="invert" to flip.</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true || value is string { Length: > 0 } s && s != "Unavailable" || value is int n && n > 0;
        if (parameter as string == "invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class StateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        "ONLINE" or true => Application.Current.Resources["Accent"],
        "STANDBY" => Application.Current.Resources["Warn"],
        "PAIR" => Application.Current.Resources["Danger"],
        _ => Application.Current.Resources["Faint"]
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class LogKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        LogKind.Success => Application.Current.Resources["Accent"],
        LogKind.Warning => Application.Current.Resources["Warn"],
        LogKind.Error => Application.Current.Resources["Danger"],
        LogKind.Command => Application.Current.Resources["Info"],
        _ => Application.Current.Resources["Dim"]
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => TvApplication.MakeInitials(value as string ?? "");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Gives every app a stable, muted tile color derived from its name.</summary>
public sealed class TileBrushConverter : IValueConverter
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xC0, 0x3A, 0x3A), Color.FromRgb(0x2F, 0x7D, 0xD1), Color.FromRgb(0x2E, 0x9E, 0x6B), Color.FromRgb(0xB0, 0x6A, 0x1F),
        Color.FromRgb(0x7A, 0x4F, 0xC9), Color.FromRgb(0x1F, 0x93, 0x9E), Color.FromRgb(0xB8, 0x3C, 0x86), Color.FromRgb(0x6B, 0x8E, 0x23)
    ];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var label = value as string ?? "";
        var known = label.ToLowerInvariant() switch
        {
            "youtube" => Color.FromRgb(0xE0, 0x2B, 0x2B),
            "netflix" => Color.FromRgb(0xC1, 0x11, 0x19),
            "prime video" => Color.FromRgb(0x1A, 0x98, 0xFF),
            "disney+" => Color.FromRgb(0x11, 0x3C, 0xCF),
            "spotify" => Color.FromRgb(0x1D, 0xB9, 0x54),
            "live tv" => Color.FromRgb(0x2E, 0x9E, 0x6B),
            "unavailable" or "" => Color.FromRgb(0x22, 0x2D, 0x24),
            _ => (Color?)null
        };
        var hash = 0;
        foreach (var c in label) hash = unchecked(hash * 31 + c);
        var color = known ?? Palette[Math.Abs(hash % Palette.Length)];
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
