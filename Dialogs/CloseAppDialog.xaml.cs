using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BatteryMonitor.Dialogs;

public sealed partial class CloseAppDialog : ContentDialog
{
    public CloseAppDialog()
    {
        this.InitializeComponent();
        this.Loaded += CloseAppDialogOnLoaded;
    }

    void CloseAppDialogOnLoaded(object sender, RoutedEventArgs e)
    {
        // A semi-transparent material that uses multiple effects, including blur and noise texture.
        // NOTE: When the timer updates the battery's brush, it will cause the acrylic translucency to be redrawn.
        root.Background = new AcrylicBrush()
        {
            TintTransitionDuration = TimeSpan.FromSeconds(0.125),
            TintColor = Microsoft.UI.Colors.Navy,
            TintLuminosityOpacity = 0.1,
            TintOpacity = 0.1f,
            FallbackColor = Windows.UI.Color.FromArgb(255,21,61,81),
            Opacity = 0.9f
        };
    }
}
