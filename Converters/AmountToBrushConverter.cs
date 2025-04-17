using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace BatteryMonitor;

/// <summary>
/// Higher amounts are considered more "risky", so the higher the dollar value the more red-shifted the color will be.
/// Color changes begin at $50 and higher.
/// </summary>
public class AmountToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        SolidColorBrush result = new(Microsoft.UI.Colors.White);

        if (App.Current.Resources.TryGetValue("PrimaryBrush", out object _))
            result = (Microsoft.UI.Xaml.Media.SolidColorBrush)App.Current.Resources["PrimaryBrush"];

        if (value == null || App.IsClosing)
            return result;

        if (value is string str && !string.IsNullOrEmpty(str))
        {
            str = str.Replace("%","").Replace("$", "");
            if (double.TryParse(str, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands | System.Globalization.NumberStyles.AllowCurrencySymbol, System.Globalization.CultureInfo.CurrentCulture, out double amnt))
            {
                switch (amnt)
                {
                    case double t when t <= 5d:  return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 10, 5));  
                    case double t when t <= 10d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 39, 17));
                    case double t when t <= 15d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 86, 17));
                    case double t when t <= 20d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 236, 102, 11));
                    case double t when t <= 25d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 139, 11));
                    case double t when t <= 30d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 165, 0));
                    case double t when t <= 35d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 184, 5));
                    case double t when t <= 40d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 201, 5));
                    case double t when t <= 45d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 229, 5));
                    case double t when t <= 50d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 251, 100));
                    default: return result;
                }
            }
        }

        return result;
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return null;
    }
}

