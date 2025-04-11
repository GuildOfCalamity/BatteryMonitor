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
        SolidColorBrush result = new(Microsoft.UI.Colors.DodgerBlue);

        if (App.Current.Resources.TryGetValue("SecondaryThemeBrush", out object _))
            result = (Microsoft.UI.Xaml.Media.SolidColorBrush)App.Current.Resources["SecondaryBrush"];

        if (value == null || App.IsClosing)
            return result;

        if (double.TryParse($"{value}", System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands | System.Globalization.NumberStyles.AllowCurrencySymbol, System.Globalization.CultureInfo.CurrentCulture, out double amnt))
        {
            switch (amnt)
            {
                #region [Original palette]
                //case double t when t >= 10000d: return new SolidColorBrush(Microsoft.UI.Colors.Red);
                //case double t when t >= 5000d:  return new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
                //case double t when t >= 1000d:  return new SolidColorBrush(Microsoft.UI.Colors.Orange);
                //case double t when t >= 500d:   return new SolidColorBrush(Microsoft.UI.Colors.Gold);
                //case double t when t >= 100d:   return new SolidColorBrush(Microsoft.UI.Colors.Yellow);
                //default: return result;
                #endregion
                case double t when t >= 10000d: return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 10, 5));  
                case double t when t >= 5000d:  return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 39, 17));
                case double t when t >= 2000d:  return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 86, 17));
                case double t when t >= 1000d:  return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 236, 102, 11));
                case double t when t >= 800d:   return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 242, 139, 11));
                case double t when t >= 600d:   return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 165, 0));
                case double t when t >= 400d:   return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 184, 5));
                case double t when t >= 200d:   return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 201, 5));
                case double t when t >= 100d:   return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 229, 5));
                case double t when t >= 50d:   return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 251, 100));
                //case double t when t >= 50d:   return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 182, 216, 88)); // normalized blend blue and yellow
                default: return result;
            }
        }

        return result;
    }

    public object? ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return null;
    }
}

