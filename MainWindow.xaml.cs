using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Content;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.Activation;
using Windows.Graphics;
using WinRT.Interop;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace BatteryMonitor;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    #region [Properties]
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

    #region [Transparency Props]
    IntPtr Handle = IntPtr.Zero; // HWND
    WINDOW_EX_STYLE WinExStyle
    {
        get => (WINDOW_EX_STYLE)NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
        set => _ = NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, (int)value);
    }
    #endregion

    #region [Dragging Props]
    int initialPointerX = 0;
    int initialPointerY = 0;
    int windowStartX = 0;
    int windowStartY = 0;
    bool isMoving = false;
    Microsoft.UI.Windowing.AppWindow appW;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto, SetLastError = true)]
    internal static extern int GetCursorPos(out Windows.Graphics.PointInt32 lpPoint);
    #endregion

    public MainWindow()
    {
        this.InitializeComponent();
        this.VisibilityChanged += MainWindowOnVisibilityChanged;
        this.Activated += MainWindowOnActivated;
        this.Closed += MainWindowOnClosed;
        //this.SizeChanged += MainWindowOnSizeChanged; // We're already using this in CreateGradientBackdrop().
        if (Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported())
        {
            this.ExtendsContentIntoTitleBar = true;
            //this.AppWindow.DefaultTitleBarShouldMatchAppModeTheme = true;
            if (App.Profile != null && App.Profile.transparency)
            {
                CustomTitleBar.Height = 0d;
                this.AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed;
            }
            else
                this.AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Standard;
            SetTitleBar(CustomTitleBar);
        }

        #region [Transparency]
        if (App.Profile != null && App.Profile.transparency)
        {
            Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinExStyle |= WINDOW_EX_STYLE.WS_EX_LAYERED; // We'll use WS_EX_LAYERED, not WS_EX_TRANSPARENT, for the effect.
            SystemBackdrop = new TransparentBackdrop();
            root.Background = new SolidColorBrush(Microsoft.UI.Colors.Green);
            root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else
        {
            CreateGradientBackdrop(root, new System.Numerics.Vector2(0.9f, 1));
        }
        #endregion

        // For programmatic minimize/maximize/restore
        _overlapPresenter = AppWindow.Presenter as OverlappedPresenter;

        // For translating screen to local Windows.Foundation.Point
        _coordinateConverter = ContentCoordinateConverter.CreateForWindowId(AppWindow.Id);

        #region [Dragging]
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Microsoft.UI.WindowId WndID = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        appW = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(WndID);
        MainGrid.PointerPressed += MainGrid_PointerPressed;
        MainGrid.PointerMoved += MainGrid_PointerMoved;
        MainGrid.PointerReleased += MainGrid_PointerReleased;
        #endregion
    }

    #region [Window Events]
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

    void MainWindowOnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (App.IsClosing)
            return;

        // Only perform update if window is visible
        if (args.WindowActivationState != WindowActivationState.Deactivated)
            SetIsAlwaysOnTop(this, App.Profile != null ? App.Profile.topmost : true);
    }

    void MainWindowOnClosed(object sender, WindowEventArgs args)
    {
        if (App.Profile == null)
            return;

        Process proc = Process.GetCurrentProcess();
        App.Profile.metrics = $"Process used {proc.PrivateMemorySize64 / 1024 / 1024}MB of memory and {proc.TotalProcessorTime.ToReadableString()} TotalProcessorTime on {Environment.ProcessorCount} possible cores.";
        App.Profile.time = DateTime.Now;
        App.Profile.version = App.GetCurrentAssemblyVersion();
        App.Profile.firstRun = false;
        ConfigHelper.SaveConfig(App.Profile);
    }
    #endregion

    void MinimizeOnClicked(object sender, RoutedEventArgs args) => _overlapPresenter?.Minimize();

    void MaximizeOnClicked(object sender, RoutedEventArgs args) => _overlapPresenter?.Maximize();

    void CloseOnClicked(object sender, RoutedEventArgs args) => this.Close(); // -or- (Application.Current as App)?.Exit();

    /// <summary>
    /// Communal event for <see cref="MenuFlyoutItem"/> clicks.
    /// The action performed will be based on the tag data.
    /// </summary>
    async void MenuFlyoutItemOnClick(object sender, RoutedEventArgs e)
    {
        var mfi = sender as MenuFlyoutItem;

        // Auto-hide if tag was passed like this ⇒ Tag="{x:Bind TitlebarMenuFlyout}"
        if (mfi is not null && mfi.Tag is not null && mfi.Tag is MenuFlyout mf) { mf?.Hide(); return; }

        if (mfi is not null && mfi.Tag is not null)
        {
            var tag = $"{mfi.Tag}";
            if (!string.IsNullOrEmpty(tag) && tag.Equals("ActionClose", StringComparison.OrdinalIgnoreCase))
            {
                if (this.Content is not null && !App.IsClosing)
                {
                    ContentDialogResult result = await DialogHelper.ShowAsync(new Dialogs.CloseAppDialog(), Content as FrameworkElement);
                    if (result is ContentDialogResult.Primary)
                    {   // The closing event may not be picked up in App.xaml.cs
                        this.Close(); // -or- (Application.Current as App)?.Exit();
                    }
                    else if (result is ContentDialogResult.None)
                    {
                        Debug.WriteLine($"[INFO] User canceled the dialog.");
                    }
                }
            }
            else if (!string.IsNullOrEmpty(tag) && tag.Equals("ActionTransparency", StringComparison.OrdinalIgnoreCase))
            {
                if (App.Profile == null)
                    return;
                
                // Toggle transparency flag
                if (App.Profile!.transparency)
                    App.Profile!.transparency = false;

                else
                    App.Profile!.transparency = true;

                // We could save later when the window close event occurs.
                ConfigHelper.SaveConfig(App.Profile);
            }
            else
            {
                Debug.WriteLine($"[WARNING] No action has been defined for '{tag}'.");
            }
        }
        else
        {
            Debug.WriteLine($"[WARNING] Tag data is empty for this MenuFlyoutItem.");
        }
    }

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

    /// <summary>
    /// Configures whether the window should always be displayed on top of other windows or not
    /// </summary>
    /// <remarks>The presenter must be an overlapped presenter.</remarks>
    /// <exception cref="NotSupportedException">Throw if the AppWindow Presenter isn't an overlapped presenter.</exception>
    /// <param name="window"><see cref="Microsoft.UI.Xaml.Window"/></param>
    /// <param name="enable">true to set always on top, false otherwise</param>
    void SetIsAlwaysOnTop(Microsoft.UI.Xaml.Window window, bool enable) => UpdateOverlappedPresenter(window, (op) => op.IsAlwaysOnTop = enable);
    void UpdateOverlappedPresenter(Microsoft.UI.Xaml.Window window, Action<Microsoft.UI.Windowing.OverlappedPresenter> action)
    {
        if (window is null)
            throw new ArgumentNullException(nameof(window));

        var appwindow = GetAppWindow(window);

        if (appwindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter overlapped)
            action(overlapped);
        else
            throw new NotSupportedException($"Not supported with a {appwindow.Presenter.Kind} presenter.");
    }

    /// <summary>
    /// Gets the <see cref="Microsoft.UI.Windowing.AppWindow"/> for the window.
    /// </summary>
    /// <param name="window"><see cref="Microsoft.UI.Xaml.Window"/></param>
    /// <returns><see cref="Microsoft.UI.Windowing.AppWindow"/></returns>
    Microsoft.UI.Windowing.AppWindow GetAppWindow(Microsoft.UI.Xaml.Window window) => GetAppWindowFromWindowHandle(WindowNative.GetWindowHandle(window));

    /// <summary>
    /// Gets the <see cref="Microsoft.UI.Windowing.AppWindow"/> from an HWND.
    /// </summary>
    /// <param name="hwnd"><see cref="IntPtr"/> of the window</param>
    /// <returns><see cref="Microsoft.UI.Windowing.AppWindow"/></returns>
    Microsoft.UI.Windowing.AppWindow GetAppWindowFromWindowHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            throw new ArgumentNullException(nameof(hwnd));

        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
    }

    #endregion

    #region [Drag Events]
    void MainGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Debug.WriteLine($"[INFO] {((Grid)sender).Name} PointerPressed");
        ((UIElement)sender).CapturePointer(e.Pointer);
        var currentPoint = e.GetCurrentPoint((UIElement)sender);
        if (currentPoint.Properties.IsLeftButtonPressed)
        {
            ((UIElement)sender).CapturePointer(e.Pointer);
            windowStartX = appW.Position.X;
            windowStartY = appW.Position.Y;
            Windows.Graphics.PointInt32 pt;
            GetCursorPos(out pt); // user32.dll
            initialPointerX = pt.X;
            initialPointerY = pt.Y;
            isMoving = true;
        }
        else if (currentPoint.Properties.IsRightButtonPressed)
        {
            if (Content is not null && Content.XamlRoot is not null)
            {
                //PointInt32 screenPoint = new PointInt32((int)currentPoint.Position.X, (int)currentPoint.Position.Y);
                //Windows.Foundation.Point localPoint = _coordinateConverter.ConvertScreenToLocal(screenPoint);
                FlyoutShowOptions options = new FlyoutShowOptions();
                options.ShowMode = FlyoutShowMode.Standard;
                options.Position = new Windows.Foundation.Point((int)currentPoint.Position.X, (int)currentPoint.Position.Y);
                if (!TitlebarMenuFlyout.IsOpen && !App.IsClosing)
                    TitlebarMenuFlyout.ShowAt(Content, options);
            }
        }
        else if (currentPoint.Properties.IsMiddleButtonPressed)
        {
            e.Handled = true;
            Application.Current.Exit();
        }
    }

    void MainGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Debug.WriteLine($"[INFO] {((Grid)sender).Name} PointerReleased");
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        isMoving = false;
    }

    void MainGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint((UIElement)sender);
        if (currentPoint.Properties.IsLeftButtonPressed)
        {
            Windows.Graphics.PointInt32 pt;
            GetCursorPos(out pt);
            if (isMoving)
                appW.Move(new Windows.Graphics.PointInt32(windowStartX + (pt.X - initialPointerX), windowStartY + (pt.Y - initialPointerY)));
        }
    }
    #endregion
}
