using System;
using System.Diagnostics;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace BatteryMonitor;


public class ColorToBrushConverter : IValueConverter
{
    /// <summary>
    /// Converts the given <paramref name="value"/> into a <see cref="SolidColorBrush"/>.
    /// Format of the color <paramref name="value"/> should be "#123456" or "#12345678".
    /// </summary>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        try
        {
            var scb = new SolidColorBrush((Windows.UI.Color)value);
            return scb;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] ColorToBrushConverter({value}): {ex.Message}");
            return new SolidColorBrush(Microsoft.UI.Colors.Red);
        }
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return null;
    }
}
