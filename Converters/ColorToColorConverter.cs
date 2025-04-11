using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace BatteryMonitor;


public class ColorToLighterColorConverter : IValueConverter
{
    /// <summary>
    /// Lightens color by <paramref name="parameter"/>.
    /// If no value is provided then 0.5 will be used.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        //Debug.WriteLine($"Converting color value {value} to lighter color.");
        var source = (Windows.UI.Color)value;

        if (parameter != null && float.TryParse($"{parameter}", out float factor))
            return source.LighterBy(factor);
        else
            return source.LighterBy(0.5F);
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return null;
    }
}


public class ColorToDarkerColorConverter : IValueConverter
{
    /// <summary>
    /// Darkens color by <paramref name="parameter"/>.
    /// If no value is provided then 0.5 will be used.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        //Debug.WriteLine($"Converting color value {value} to darker color.");
        var source = (Windows.UI.Color)value;

        if (parameter != null && float.TryParse($"{parameter}", out float factor))
            return source.DarkerBy(factor);
        else
            return source.DarkerBy(0.5F);
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return null;
    }
}

public class ColorToLighterBrushConverter : IValueConverter
{
    /// <summary>
    /// Lightens color by <paramref name="parameter"/>.
    /// If no value is provided then 0.5 will be used.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        //Debug.WriteLine($"Converting color value {value} to lighter color.");
        var source = (Windows.UI.Color)value;

        if (parameter != null && float.TryParse($"{parameter}", out float factor))
            return new SolidColorBrush(source.LighterBy(factor));
        else
            return new SolidColorBrush(source.LighterBy(0.5F));
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return null;
    }
}

public class ColorToDarkerBrushConverter : IValueConverter
{
    /// <summary>
    /// Darkens color by <paramref name="parameter"/>.
    /// If no value is provided then 0.5 will be used.
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        //Debug.WriteLine($"Converting color value {value} to darker color.");
        var source = (Windows.UI.Color)value;

        if (parameter != null && float.TryParse($"{parameter}", out float factor))
            return new SolidColorBrush(source.DarkerBy(factor));
        else
            return new SolidColorBrush(source.DarkerBy(0.5F));
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return null;
    }
}
