using System;
using System.ComponentModel;
using System.Diagnostics;

using Microsoft.UI.Content;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace BatteryMonitor;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    #region [Props]
    static bool _firstVisible = false;
    ContentCoordinateConverter _coordinateConverter;
    OverlappedPresenter? _overlapPresenter;

    public event PropertyChangedEventHandler? PropertyChanged;
    bool _isBusy = false;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            _isBusy = value;
            NotifyPropertyChanged(nameof(IsBusy));
        }
    }

    public void NotifyPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        if (string.IsNullOrEmpty(propertyName)) { return; }
        // Confirm that we're on the UI thread in the event that DependencyProperty is changed under forked thread.
        DispatcherQueue.InvokeOnUI(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }

    #endregion

    public MainWindow()
    {
        this.InitializeComponent();
        this.VisibilityChanged += MainWindowOnVisibilityChanged;
        //this.SizeChanged += MainWindowOnSizeChanged; // We're already using this in CreateGradientBackdrop().
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            this.ExtendsContentIntoTitleBar = true;
            //this.AppWindow.DefaultTitleBarShouldMatchAppModeTheme = true;
            this.AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
            SetTitleBar(CustomTitleBar);
        }
        CreateGradientBackdrop(root, new System.Numerics.Vector2(0.9f, 1));

        // For programmatic minimize/maximize/restore
        _overlapPresenter = AppWindow.Presenter as OverlappedPresenter;

        // For translating screen to local Windows.Foundation.Point
        _coordinateConverter = ContentCoordinateConverter.CreateForWindowId(AppWindow.Id);
    }

    /// <summary>
    /// An impromptu OnLoaded event. 
    /// It would be better to read from the AppWin.Changed event, but this works fine.
    /// </summary>
    void MainWindowOnVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        if (!_firstVisible && this.Content != null)
        {
            Debug.WriteLine($"[INFO] MainWindow First Visible");
        }
        _firstVisible = true;
    }

    void MinimizeOnClicked(object sender, RoutedEventArgs args) => _overlapPresenter?.Minimize();

    void MaximizeOnClicked(object sender, RoutedEventArgs args) => _overlapPresenter?.Maximize();

    void CloseOnClicked(object sender, RoutedEventArgs args) => this.Close(); // -or- (Application.Current as App)?.Exit();

    #region [Helpers]

    void CreateGradientBackdrop(FrameworkElement fe, System.Numerics.Vector2 endPoint)
    {
        // Get the FrameworkElement's compositor.
        var compositor = ElementCompositionPreview.GetElementVisual(fe).Compositor;
        if (compositor == null) { return; }
        var gb = compositor.CreateLinearGradientBrush();

        // Define gradient stops.
        var gradientStops = gb.ColorStops;

        // If we found our App.xaml brushes then use them.
        if (App.Current.Resources.TryGetValue("GC1", out object clr1) &&
            App.Current.Resources.TryGetValue("GC2", out object clr2) &&
            App.Current.Resources.TryGetValue("GC3", out object clr3) &&
            App.Current.Resources.TryGetValue("GC4", out object clr4))
        {
            gradientStops.Insert(0, compositor.CreateColorGradientStop(0.0f, (Windows.UI.Color)clr1));
            gradientStops.Insert(1, compositor.CreateColorGradientStop(0.3f, (Windows.UI.Color)clr2));
            gradientStops.Insert(2, compositor.CreateColorGradientStop(0.6f, (Windows.UI.Color)clr3));
            gradientStops.Insert(3, compositor.CreateColorGradientStop(1.0f, (Windows.UI.Color)clr4));
        }
        else
        {
            gradientStops.Insert(0, compositor.CreateColorGradientStop(0.0f, Windows.UI.Color.FromArgb(55, 255, 0, 0)));   // Red
            gradientStops.Insert(1, compositor.CreateColorGradientStop(0.3f, Windows.UI.Color.FromArgb(55, 255, 216, 0))); // Yellow
            gradientStops.Insert(2, compositor.CreateColorGradientStop(0.6f, Windows.UI.Color.FromArgb(55, 0, 255, 0)));   // Green
            gradientStops.Insert(3, compositor.CreateColorGradientStop(1.0f, Windows.UI.Color.FromArgb(55, 0, 0, 255)));   // Blue
        }

        // Set the direction of the gradient.
        gb.StartPoint = new System.Numerics.Vector2(0, 0);
        //gb.EndPoint = new System.Numerics.Vector2(1, 1);
        gb.EndPoint = endPoint;

        // Create a sprite visual and assign the gradient brush.
        var spriteVisual = Compositor.CreateSpriteVisual();
        spriteVisual.Brush = gb;

        // Set the size of the sprite visual to cover the entire window.
        spriteVisual.Size = new System.Numerics.Vector2((float)fe.ActualSize.X, (float)fe.ActualSize.Y);

        // Handle the SizeChanged event to adjust the size of the sprite visual when the window is resized.
        fe.SizeChanged += (s, e) =>
        {
            spriteVisual.Size = new System.Numerics.Vector2((float)fe.ActualWidth, (float)fe.ActualHeight);
        };

        // Set the sprite visual as the background of the FrameworkElement.
        ElementCompositionPreview.SetElementChildVisual(fe, spriteVisual);
    }
    #endregion
}
