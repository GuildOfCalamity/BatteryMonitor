using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.AppLifecycle;

using Windows.ApplicationModel.Activation;
using Windows.Foundation.Metadata;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.Storage;
using Windows.System;
using Windows.UI.ViewManagement;

namespace BatteryMonitor;

/// <summary>
/// There's some activation and jumplist goodies included below.
/// </summary>
public partial class App : Application
{
    #region [Props]
    // NOTE: If you would like to deploy this app as "Packaged", then open the csproj and change
    //  <WindowsPackageType>None</WindowsPackageType> to <WindowsPackageType>MSIX</WindowsPackageType>
    // https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/#advantages-and-disadvantages-of-packaging-your-app
#if IS_UNPACKAGED // We're using a custom PropertyGroup condition we defined in the csproj to help us with the decision.
    public static bool IsPackaged { get => false; }
#else
    public static bool IsPackaged { get => true; }
#endif

    public static Window? m_window;
    public static int m_width { get; set; } = 800;
    public static int m_height { get; set; } = 550;
    public static bool IsClosing { get; set; } = false;
    public static string BaseFolder { get; private set; }
    public static FrameworkElement? MainRoot { get; set; }
    public static IntPtr WindowHandle { get; set; }
    public static Config? Profile { get; set; }
    public static AppWindow? AppWin { get; set; }
    public static Version WindowsVersion => Extensions.GetWindowsVersionUsingAnalyticsInfo();
    public static bool IsWindowMaximized { get; set; }
    public static bool IsMainInstance { get; private set; }
    public static Mutex? InstanceMutex { get; private set; }
    public static FontFamily? EmbeddedFont { get; private set; }
    public static Microsoft.Windows.AppLifecycle.AppActivationArguments? ActivationArgs { get; set; }
    static UISettings m_UISettings = new UISettings();
    static EasClientDeviceInformation m_deviceInfo = new EasClientDeviceInformation();
    public static List<string> ArgList = new();
    public static Dictionary<string, string> MachineEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static List<string>? AssemblyReferences { get; private set; }
    public static Action<Windows.Graphics.SizeInt32>? WindowSizeChanged { get; set; }

    public static CancellationTokenSource? CoreChannelToken;
    static ValueStopwatch StopWatch { get; set; }

    /// <summary>
    /// Starting with the Windows 10 Fall Creators Update, Visual Studio provides a new 
    ///   XAML designer that targets the Windows 10 Fall Creators Update and later.
    /// Windows.ApplicationModel.DesignMode.DesignModeEnabled returns true when called 
    ///   from user code running inside any version of the XAML designer, regardless of 
    ///   which SDK version you target.
    /// Use Windows.ApplicationModel.DesignMode.DesignMode2Enabled to differentiate code 
    ///   that depends on functionality only enabled for a XAML designer that targets the 
    ///   Windows 10 Fall Creators Update SDK or later.
    /// </summary>
    public static bool IsDesignModeEnabled => Windows.ApplicationModel.DesignMode.DesignModeEnabled;
    public static bool IsDesignMode2Enabled => Windows.ApplicationModel.DesignMode.DesignMode2Enabled;

    #region [User preferences from Windows.UI.ViewManagement]
    // We won't configure backing fields for these as the user could adjust them during app lifetime.
    public static bool TransparencyEffectsEnabled
    {
        get => m_UISettings.AdvancedEffectsEnabled;
    }
    public static bool AnimationsEffectsEnabled
    {
        get => m_UISettings.AnimationsEnabled;
    }
    public static bool AutoHideScrollbars
    {
        get
        {
            if (WindowsVersion.Major >= 10 && WindowsVersion.Build >= 18362)
                return m_UISettings.AutoHideScrollBars;
            else
                return true;
        }
    }
    public static double TextScaleFactor
    {
        get => m_UISettings.TextScaleFactor;
    }
    #endregion

    #region [Machine info from Windows.Security.ExchangeActiveSyncProvisioning]
    public static string OperatingSystem
    {
        get => m_deviceInfo.OperatingSystem;
    }
    public static string DeviceManufacturer
    {
        get => m_deviceInfo.SystemManufacturer;
    }
    public static string DeviceModel
    {
        get => m_deviceInfo.SystemProductName;
    }
    public static string MachineName
    {
        get => m_deviceInfo.FriendlyName;
    }
    public static string MachineSku
    {
        get => m_deviceInfo.SystemSku;
    }
    #endregion

    #endregion

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        #region [Exception handlers]
        AppDomain.CurrentDomain.UnhandledException += CurrentDomainUnhandledException;
        AppDomain.CurrentDomain.FirstChanceException += CurrentDomainFirstChanceException;
        AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        UnhandledException += ApplicationUnhandledException;
        if (Debugger.IsAttached)
        {
            DebugSettings.BindingFailed += DebugOnBindingFailed;
            DebugSettings.XamlResourceReferenceFailed += DebugOnXamlResourceReferenceFailed;
        }
        #endregion

        StopWatch = ValueStopwatch.StartNew();
        BaseFolder = AppDomain.CurrentDomain.BaseDirectory;

        // Is there more than one of us?
        InstanceMutex = new Mutex(true, GetCurrentAssemblyName(), out bool isNew);
        if (isNew)
        {
            IsMainInstance = true;
        }
        else
        {
            //InstanceMutex.Close();
            CloseExistingInstanceAlt();
        }
        
        GatherEnvironment();

        this.InitializeComponent();

        // https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.focusvisualkind?view=windows-app-sdk-1.3
        this.FocusVisualKind = FocusVisualKind.Reveal;

        AssemblyReferences = Extensions.GatherReferenceAssemblies(true);

        bool areWeTheRealDeal = ApiInformation.IsPropertyPresent("Microsoft.UI.Dispatching.DispatcherQueue", "HasThreadAccess");

        #region [DataReader/DataWriter Test]
        _ = Task.Run(async () =>
        {
            /*
            [DateTime]
                Represents a point in time without any information about the time zone. 
                It's a naive representation of a date and time.
            
            [DateTimeOffset]
                Represents a point in time with an offset from Coordinated Universal Time (UTC). 
                It captures both the date and time, as well as the time zone information.
            */

            var tzi = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            DateTimeOffset dto = new DateTimeOffset(DateTime.Now, tzi.GetUtcOffset(DateTime.Now));

            await Extensions.WriteDTOUsingDataWriterAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "StreamedFileAsync.txt"), dto);
            await Task.Delay(100);
            dto = await Extensions.ReadDTOUsingDataReaderAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "StreamedFileAsync.txt"));
            if (dto == DateTimeOffset.MinValue)
                Debug.WriteLine($"[FAILED]");

            var bytes = await Extensions.ReadBytesUsingDataReaderAsync(System.IO.Path.Combine(AppContext.BaseDirectory, "StreamedFileAsync.txt"));
            var xfer = await Extensions.TransferDataInMemoryStream("this,is,some,tokenized,values,to,test,in,memory,stream", ",");
            Debug.WriteLine($"{xfer}");
        });
        #endregion
    }

    /// <summary>
    /// EnvironmentVariableTarget has three options:
    ///     1) Machine
    ///     2) Process
    ///     3) User
    /// </summary>
    void GatherEnvironment()
    {
        // Get the environment variables.
        IDictionary procVars = GetEnvironmentVariablesWithErrorLog(EnvironmentVariableTarget.Process);
        // Adding names and variables that exist.
        foreach (DictionaryEntry pVar in procVars)
        {
            string? pVarKey = (string?)pVar.Key;
            string? pVarValue = (string?)pVar.Value ?? "";
            if (!string.IsNullOrEmpty(pVarKey) && !MachineEnvironment.ContainsKey(pVarKey))
            {
                MachineEnvironment.Add(pVarKey, pVarValue);
            }
        }
    }

    /// <summary>
    /// Returns the variables for the specified target. Errors that occurs will be caught and logged.
    /// </summary>
    /// <param name="target">The target variable source of the type <see cref="EnvironmentVariableTarget"/> </param>
    /// <returns>A dictionary with the variable or an empty dictionary on errors.</returns>
    IDictionary GetEnvironmentVariablesWithErrorLog(EnvironmentVariableTarget target)
    {
        try
        {
            return Environment.GetEnvironmentVariables(target);
        }
        catch (Exception ex)
        {
            DebugLog($"Exception while getting the environment variables for target '{target}': {ex.Message}");
            return new Hashtable(); // HashTable inherits from IDictionary
        }
    }

    public static void CloseExistingInstance()
    {
        if (Debugger.IsAttached)
        {
            DebugLog($"Skipping kill since debugger is attached.");
            return;
        }

        try
        {
            var name = AppDomain.CurrentDomain.FriendlyName;
            Process[] pname = Process.GetProcessesByName(name);
            if (pname.Length > 1)
            {
                DebugLog($"Killing existing app '{name}'");
                pname.Where(p => p.Id != Process.GetCurrentProcess().Id).First().Kill();
            }
        }
        catch (Exception ex)
        {
            DebugLog($"CloseExistingApp: {ex.Message}");
        }
    }

    /// <summary>
    /// Similar to <see cref="CloseExistingInstance"/>.
    /// </summary>
    public static void CloseExistingInstanceAlt()
    {
        if (Debugger.IsAttached)
        {
            DebugLog($"Skipping kill since debugger is attached.");
            return;
        }

        try
        {
            int currentId = Process.GetCurrentProcess().Id; // Get the current process ID.
            Process[] processes = Process.GetProcessesByName(AppDomain.CurrentDomain.FriendlyName);
            foreach (var process in processes)
            {
                if (process.Id != currentId)
                    process.Kill();
            }
        }
        catch (Exception ex)
        {
            DebugLog($"CloseExistingInstanceAlt: {ex.Message}");
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        #region [Determining traditional arguments]
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length > 1) // The first element will be "AssemblyName.dll"
        {
            var array = cmdArgs.IgnoreFirstTakeRest();
            foreach (var item in array)
            {
                DebugLog($"Adding argument: {item}"); // e.g. "JumpList-Purge"
                ArgList.Add($"{item}");
            }
        }
        #endregion

        m_window = new MainWindow();

        #region [Determining AppInstance activation kind]
        bool isRedirect = false;
        var currentInstance = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent();
        if (currentInstance != null)
        {
            // IActivatedEventArgs are mostly for UWP
            ActivationArgs = currentInstance.GetActivatedEventArgs();
            currentInstance.Activated += InstanceOnRedirectedActivated;

            // Find out what kind of activation this is.
            if (ActivationArgs.Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
            {
                var fileActivationArguments = ActivationArgs.Data as Windows.ApplicationModel.Activation.FileActivatedEventArgs;
                DebugLog($"[AppActivation] {fileActivationArguments?.Files[0].Path}");
                // This is a file activation: here we'll get the file information,
                // and register the file name as our instance key.
                if (ActivationArgs.Data is IFileActivatedEventArgs fileArgs)
                {
                    IStorageItem file = fileArgs.Files[0];
                    var keyInstance = Microsoft.Windows.AppLifecycle.AppInstance.FindOrRegisterForKey(file.Name);
                    // If we successfully registered the file name, we must be the
                    // only instance running that was activated for this file.
                    if (keyInstance != null && !keyInstance.IsCurrent)
                    {
                        isRedirect = true;
                        keyInstance.RedirectActivationToAsync(ActivationArgs).GetAwaiter().GetResult();
                    }
                }
            }
            else
            {
                DebugLog($"[AppActivation] ActivationKind => {ActivationArgs.Kind}");
                AnalyzeActivationData(ActivationArgs.Data);
            }
        }


        #endregion

        if (ConfigHelper.DoesConfigExist())
            Profile = ConfigHelper.LoadConfig();

        AppWin = GetAppWindow(m_window);

        if (AppWin != null)
        {
            // Gets or sets a value that indicates whether this window will appear in various system representations, such as ALT+TAB and taskbar.
            AppWin.IsShownInSwitchers = true;

            // We don't have the Closing event exposed by default, so we'll use the AppWindow to compensate.
            AppWin.Closing += (s, e) =>
            {
                Debug.WriteLine($"[INFO] Application closing detected at {DateTime.Now.ToString("hh:mm:ss.fff tt")}");
                
                App.IsClosing = true;
                
                if (CoreChannelToken is not null)
                    CoreChannelToken.Cancel();

                if (Profile is not null)
                {
                    Process proc = Process.GetCurrentProcess();
                    Profile!.metrics = $"Process used {proc.PrivateMemorySize64 / 1024 / 1024}MB of memory and {proc.TotalProcessorTime.ToReadableString()} TotalProcessorTime on {Environment.ProcessorCount} possible cores.";
                    Profile!.time = DateTime.Now;
                    Profile!.version = GetCurrentAssemblyVersion();
                    ConfigHelper.SaveConfig(Profile);
                }
            };

            // Destroying is always called, but Closing is only called when the application is shutdown normally.
            AppWin.Destroying += (s, e) =>
            {
                Debug.WriteLine($"[INFO] Application destroying detected at {DateTime.Now.ToString("hh:mm:ss.fff tt")}");
            };

            // The changed event contains the valuables, such as: position, size, visibility, z-order and presenter.
            AppWin.Changed += (s, args) =>
            {
                if (args.DidSizeChange)
                {
                    Debug.WriteLine($"[INFO] Window size changed to {s.Size.Width},{s.Size.Height}");
                    WindowSizeChanged?.Invoke(s.Size);
                    if (s.Presenter is not null && s.Presenter is OverlappedPresenter op)
                        IsWindowMaximized = op.State is OverlappedPresenterState.Maximized;

                    if (!IsWindowMaximized && Profile is not null)
                    {
                        // Update width and height for profile settings.
                        Profile!.windowHeight = s.Size.Height;
                        Profile!.windowWidth = s.Size.Width;
                    }
                }

                if (args.DidPositionChange)
                {
                    if (s.Position.X > 0 && s.Position.Y > 0)
                    {
                        // This property is initially null. Once a window has been shown it always has a
                        // presenter applied, either one applied by the platform or applied by the app itself.
                        if (s.Presenter is not null && s.Presenter is OverlappedPresenter op)
                        {
                            if (op.State == OverlappedPresenterState.Minimized)
                            {
                                Debug.WriteLine($"[INFO] Window minimized");
                            }
                            else if (op.State != OverlappedPresenterState.Maximized && Profile is not null)
                            {
                                Debug.WriteLine($"[INFO] Updating window position to {s.Position.X},{s.Position.Y} and size to {s.Size.Width},{s.Size.Height}");
                                // Update X and Y for profile settings.
                                Profile!.windowLeft = s.Position.X;
                                Profile!.windowTop = s.Position.Y;
                            }
                            else
                            {
                                Debug.WriteLine($"[INFO] Ignoring position saving (window maximized or restored)");
                            }
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[INFO] Ignoring zero/negative positional values");
                    }
                }
            };

            // Set the application icon.
            if (IsPackaged)
                AppWin.SetIcon(System.IO.Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, $"Assets/AppIcon.ico"));
            else
                AppWin.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, $"Assets/AppIcon.ico"));

            AppWin.TitleBar.IconShowOptions = IconShowOptions.ShowIconAndSystemMenu;
        }

        m_window.Activate();
        
        // Save the FrameworkElement for any future content dialogs.
        MainRoot = m_window.Content as FrameworkElement;

        // Update settings with examples if it's new.
        if (Profile is null || string.IsNullOrEmpty(Profile.version))
        {
            Profile = new Config
            {
                firstRun = true,
                logging = false,
                windowLeft = 100,
                windowTop = 100,
                windowWidth = m_width,
                windowHeight = m_height,
            };
            ConfigHelper.SaveConfig(Profile);
            AppWin?.Resize(new Windows.Graphics.SizeInt32(m_width, m_height));
            CenterWindow(m_window);
        }
        else
        {
            // User's monitor setup could change on next run so verify that we're
            // not trying to place the window on a monitor that no longer exists.
            var displayArea = GetDisplayArea(m_window);
            if (displayArea != null)
            {
                var monitorCount = GetMonitorCount();
                if (Profile.windowLeft >= (displayArea.OuterBounds.Width * monitorCount))
                {
                    Profile.windowLeft = 100;
                    DebugLog($"Current setting would cause window to appear outside display bounds, resetting to {Profile.windowLeft}.");
                }
                else
                {
                    DebugLog($"Display area bounds: {displayArea.OuterBounds.Width * monitorCount},{displayArea.OuterBounds.Height}");
                }
            }
            AppWin?.MoveAndResize(new Windows.Graphics.RectInt32(Profile.windowLeft, Profile.windowTop, Profile.windowWidth, Profile.windowHeight), Microsoft.UI.Windowing.DisplayArea.Primary);
        }

        Debug.WriteLine($"••••••••••••••••••••••••••••••••••••••••••••");
        Debug.WriteLine($"   Startup took {StopWatch.GetElapsedFriendly()} ");
        Debug.WriteLine($"••••••••••••••••••••••••••••••••••••••••••••");
    }

    #region [Window Helpers]
    /// <summary>
    /// This code example demonstrates how to retrieve an AppWindow from a WinUI3 window.
    /// The AppWindow class is available for any top-level HWND in your app.
    /// AppWindow is available only to desktop apps (both packaged and unpackaged), it's not available to UWP apps.
    /// https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/windowing/windowing-overview
    /// https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.windowing.appwindow.create?view=windows-app-sdk-1.3
    /// </summary>
    public Microsoft.UI.Windowing.AppWindow? GetAppWindow(object window)
    {
        // Retrieve the window handle (HWND) of the current (XAML) WinUI3 window.
        var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        // For other classes to use (mostly P/Invoke).
        App.WindowHandle = hWnd;

        // Retrieve the WindowId that corresponds to hWnd.
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);

        // Lastly, retrieve the AppWindow for the current (XAML) WinUI3 window.
        Microsoft.UI.Windowing.AppWindow appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

        return appWindow;
    }

    /// <summary>
    /// If <see cref="App.WindowHandle"/> is set then a call to User32 <see cref="SetForegroundWindow(nint)"/> 
    /// will be invoked. I tried using the native OverlappedPresenter.Restore(true), but that does not work.
    /// </summary>
    public static void ActivateMainWindow()
    {
        if (App.WindowHandle != IntPtr.Zero)
        {
            //_ = SetForegroundWindow(App.WindowHandle);
        }

        if (AppWin is not null && AppWin.Presenter is not null && AppWin.Presenter is OverlappedPresenter op)
        {
            op.Restore(true);
        }
    }

    /// <summary>
    /// Centers a <see cref="Microsoft.UI.Xaml.Window"/> based on the <see cref="Microsoft.UI.Windowing.DisplayArea"/>.
    /// </summary>
    /// <remarks>This must be run on the UI thread.</remarks>
    public static void CenterWindow(Window window)
    {
        if (window == null) { return; }

        try
        {
            System.IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            if (Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId) is Microsoft.UI.Windowing.AppWindow appWindow &&
                Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest) is Microsoft.UI.Windowing.DisplayArea displayArea)
            {
                Windows.Graphics.PointInt32 CenteredPosition = appWindow.Position;
                CenteredPosition.X = (displayArea.WorkArea.Width - appWindow.Size.Width) / 2;
                CenteredPosition.Y = (displayArea.WorkArea.Height - appWindow.Size.Height) / 2;
                appWindow.Move(CenteredPosition);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] {MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The <see cref="Microsoft.UI.Windowing.DisplayArea"/> exposes properties such as:
    /// OuterBounds     (Rect32)
    /// WorkArea.Width  (int)
    /// WorkArea.Height (int)
    /// IsPrimary       (bool)
    /// DisplayId.Value (ulong)
    /// </summary>
    /// <param name="window"></param>
    /// <returns><see cref="DisplayArea"/></returns>
    public Microsoft.UI.Windowing.DisplayArea? GetDisplayArea(Window window)
    {
        try
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            return Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(windowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] {MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gets the current state of the given window.
    /// </summary>
    /// <param name="window">The <see cref="Microsoft.UI.Xaml.Window"/> to check.</param>
    /// <returns>"Maximized","Minimized","Restored","FullScreen","Unknown"</returns>
    /// <remarks>The "Restored" state is equivalent to "Normal" in a WinForm app.</remarks>
    public string GetWindowState(Window window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        var appWindow = GetAppWindow(window);

        if (appWindow is null)
            return "Unknown";

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            return presenter.State switch
            {
                OverlappedPresenterState.Maximized => "Maximized",
                OverlappedPresenterState.Minimized => "Minimized",
                OverlappedPresenterState.Restored => "Restored",
                _ => "Unknown"
            };
        }

        // If it's not an OverlappedPresenter, check for FullScreen mode.
        if (appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            return "FullScreen";

        return "Unknown";
    }

    /// <summary>
    /// Checks if the given window is maximized on its current monitor.
    /// </summary>
    /// <param name="window">The WinUI 3 window to check.</param>
    /// <returns>true if the window is maximized on its current monitor, false otherwise</returns>
    public bool IsWindowMaximizedOnCurrentMonitor(Window window)
    {
        if (window == null)
            throw new ArgumentNullException(nameof(window));

        // Get the current window bounds
        var windowBounds = window.Bounds;

        // Get the monitor that the window is currently on
        var displayArea = GetDisplayArea(window);
        if (displayArea == null)
        {
            throw new InvalidOperationException("Could not determine the monitor where the window is located.");
        }

        // Get the work area (visible bounds) of the monitor
        var workArea = displayArea.WorkArea;

        // Allow for minor differences due to DPI scaling
        const double tolerance = 2.0;

        return Math.Abs(windowBounds.Width - workArea.Width) < tolerance &&
               Math.Abs(windowBounds.Height - workArea.Height) < tolerance;
    }

    /// <summary>
    /// To my knowledge there is no way to get this natively via the WinUI3 SDK, so I'm adding a P/Invoke.
    /// </summary>
    /// <returns>the amount of displays the system recognizes</returns>
    public static int GetMonitorCount()
    {
        int count = 0;

        MonitorEnumProc callback = (IntPtr hDesktop, IntPtr hdc, ref ScreenRect prect, int d) => ++count > 0;

        if (EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, 0))
        {
            Debug.WriteLine($"[INFO] You have {count} {(count > 1 ? "monitors" : "monitor")}.");
            return count;
        }
        else
        {
            Debug.WriteLine("[WARNING] An error occurred while enumerating monitors.");
            return 1;
        }
    }
 
    [StructLayout(LayoutKind.Sequential)]
    struct ScreenRect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
    delegate bool MonitorEnumProc(IntPtr hDesktop, IntPtr hdc, ref ScreenRect pRect, int dwData);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lpRect, MonitorEnumProc callback, int dwData);
    #endregion

    #region [Reflection Helpers]
    /// <summary>
    /// Returns the declaring type's namespace.
    /// </summary>
    public static string? GetCurrentNamespace() => System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType?.Namespace;

    /// <summary>
    /// Returns the declaring type's namespace.
    /// </summary>
    public static string? GetFormattedNamespace() => System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType?.Namespace?.Replace("_", " "); // Namespace?.SeparateCamelCase();

    /// <summary>
    /// Returns the declaring type's full name.
    /// </summary>
    public static string? GetCurrentFullName() => System.Reflection.MethodBase.GetCurrentMethod()?.DeclaringType?.Assembly.FullName;

    /// <summary>
    /// Returns the declaring type's assembly name.
    /// </summary>
    public static string? GetCurrentAssemblyName() => System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;

    /// <summary>
    /// Returns the AssemblyVersion, not the FileVersion.
    /// </summary>
    public static string? GetCurrentAssemblyVersion() => $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}";

    /// <summary>
    /// Returns the current assembly's folder location.
    /// </summary>
    public static string GetCurrentDirectory() => System.IO.Path.GetDirectoryName(Environment.ProcessPath)!;

    public static string GetAppRuntime()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(AssemblyReferences?.Where(ar => ar.StartsWith("Microsoft.WindowsAppRuntime")).FirstOrDefault() ?? "N/A");
        sb.Append(AssemblyReferences?.Where(ar => ar.StartsWith("Microsoft.WinUI")).FirstOrDefault() ?? "N/A");
        return sb.ToString();
    }
    #endregion

    #region [Dialog Helper]
    static SemaphoreSlim semaSlim = new SemaphoreSlim(1, 1);
    /// <summary>
    /// The <see cref="Microsoft.UI.Xaml.Controls.ContentDialog"/> looks much better than the
    /// <see cref="Windows.UI.Popups.MessageDialog"/> and is part of the native Microsoft.UI.Xaml.Controls.
    /// The <see cref="Microsoft.UI.Xaml.Controls.ContentDialog"/> does not offer a <see cref="Windows.UI.Popups.UICommandInvokedHandler"/>
    /// callback, but in this example I've replaced that functionality with actions. Both can be shown asynchronously.
    /// </summary>
    /// <remarks>
    /// There is no need to call <see cref="WinRT.Interop.InitializeWithWindow.Initialize"/> when using the <see cref="Microsoft.UI.Xaml.Controls.ContentDialog"/>,
    /// but a <see cref="Microsoft.UI.Xaml.XamlRoot"/> must be defined since it inherits from <see cref="Microsoft.UI.Xaml.Controls.Control"/>.
    /// The <see cref="SemaphoreSlim"/> was added to prevent "COMException: Only one ContentDialog can be opened at a time."
    /// </remarks>
    public static async Task ShowContentDialog(string title, string message, string primaryText, string cancelText, double minWidth, Action? onPrimary, Action? onCancel, Uri? imageUri)
    {
        if (App.MainRoot?.XamlRoot == null) { return; }

        await semaSlim.WaitAsync();

        #region [Initialize Assets]
        double fontSize = 14;
        double brdrThickness = 4;
        if (minWidth <= 0)
            minWidth = 400;

        Microsoft.UI.Xaml.Media.FontFamily fontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe");
        Microsoft.UI.Xaml.Media.Brush brdrBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20));

        if (EmbeddedFont is not null)
            fontFamily = EmbeddedFont;

        if (App.Current.Resources.TryGetValue("FontSizeMedium", out object _))
            fontSize = (double)App.Current.Resources["FontSizeMedium"];

        if (App.Current.Resources.TryGetValue("PrimaryFont", out object _))
            fontFamily = (Microsoft.UI.Xaml.Media.FontFamily)App.Current.Resources["PrimaryFont"];

        if (App.Current.Resources.TryGetValue("GradientBarBrush", out object _))
            brdrBrush = (Microsoft.UI.Xaml.Media.Brush)App.Current.Resources["GradientBarBrush"];

        StackPanel panel = new StackPanel()
        {
            Orientation = Microsoft.UI.Xaml.Controls.Orientation.Vertical,
            Spacing = 10d
        };

        if (imageUri is not null)
        {
            panel.Children.Add(new Image
            {
                Margin = new Thickness(1, -50, 1, 1), // Move the image into the title area.
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Right,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Width = 40,
                Height = 40,
                Source = new BitmapImage(imageUri)
            });
        }

        panel.Children.Add(new TextBlock()
        {
            Text = message,
            FontSize = fontSize,
            FontFamily = fontFamily,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap
        });

        var tb = new TextBox()
        {
            Text = message,
            MinWidth = minWidth,
            FontSize = fontSize,
            FontFamily = fontFamily,
            TextWrapping = TextWrapping.Wrap
        };
        tb.Loaded += (s, e) => { tb.SelectAll(); };
        #endregion

        // NOTE: Content dialogs will automatically darken the background.
        ContentDialog contentDialog = new ContentDialog()
        {
            Title = title,
            MinWidth = minWidth + brdrThickness,
            BorderBrush = brdrBrush,
            BorderThickness = new Thickness(brdrThickness),
            PrimaryButtonText = primaryText,
            CloseButtonText = cancelText,
            Content = panel,
            XamlRoot = App.MainRoot?.XamlRoot,
            RequestedTheme = App.MainRoot?.ActualTheme ?? ElementTheme.Default
        };

        try
        {
            ContentDialogResult result = await contentDialog.ShowAsync();

            switch (result)
            {
                case ContentDialogResult.Primary:
                    onPrimary?.Invoke();
                    break;
                //case ContentDialogResult.Secondary:
                //    onSecondary?.Invoke();
                //    break;
                case ContentDialogResult.None: // Cancel
                    onCancel?.Invoke();
                    break;
                default:
                    Debug.WriteLine($"Dialog result not defined.");
                    break;
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            Debug.WriteLine($"[ERROR] ShowDialogBox(HRESULT={ex.ErrorCode}): {ex.Message}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] ShowDialogBox: {ex.Message}");
        }
        finally
        {
            semaSlim.Release();
        }
    }
    #endregion

    #region [Key State Helpers]
    public static bool IsCtrlKeyDown()
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    public static bool IsAltKeyDown()
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    public static bool IsCapsLockOn()
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.CapitalLock);
        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Locked);
    }
    #endregion

    #region [Domain Events]
    void ApplicationUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // https://docs.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.unhandledexception.
        Exception? ex = e.Exception;
        Debug.WriteLine($"[UnhandledException]: {ex?.Message}");
        Debug.WriteLine($"Unhandled exception of type {ex?.GetType()}: {ex}");
        DebugLog($"Unhandled Exception StackTrace: {Environment.StackTrace}");
        e.Handled = true;
    }

    void CurrentDomainFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        Debug.WriteLine($"[ERROR] First chance exception from {sender?.GetType()}: {e.Exception.Message}");
        // Ignore profile encryption property tests and fake ItemsSourceProperty binding warnings.
        if (!string.IsNullOrEmpty(e.Exception.Message) && !e.Exception.Message.Contains("The input is not a valid Base-64 string") && !e.Exception.Message.Contains("'WinRT.IInspectable' to type 'System.String'"))
        {
            DebugLog($"First chance exception from {sender?.GetType()}: {e.Exception.Message}");
            if (e.Exception.InnerException != null)
                DebugLog($"  ⇨ InnerException: {e.Exception.InnerException.Message}");
            DebugLog($"  ⇨ StackTrace: {Environment.StackTrace}");
        }
    }

    void CurrentDomainUnhandledException(object? sender, System.UnhandledExceptionEventArgs e)
    {
        Exception? ex = e.ExceptionObject as Exception;
        Debug.WriteLine($"[ERROR] Thread exception of type {ex?.GetType()}: {ex}");
        DebugLog($"Thread exception of type {ex?.GetType()}: {ex}");
    }

    void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (sender != null && sender is Task tsk)
        {
            Debug.WriteLine($"[TaskException] {tsk.Status}");
        }

        if (e.Exception is AggregateException aex)
        {
            if (!string.IsNullOrEmpty(aex?.Message))
            {
                Debug.WriteLine($"[TaskException] {aex?.Message}");
                DebugLog($"Task exception: {aex?.Message}");
            }

            aex?.Flatten().Handle(ex =>
            {
                Debug.WriteLine($"[ERROR] Unobserved task exception: {ex?.Message}");
                DebugLog($"Unobserved task exception: {ex?.Message}");
                return true;
            });
        }
        e.SetObserved(); // suppress and handle manually
    }

    void CurrentDomainOnProcessExit(object? sender, EventArgs e)
    {
        if (!IsClosing)
            IsClosing = true;

        if (sender is null)
            return;

        if (sender is AppDomain ad)
        {
            Debug.WriteLine($"[OnProcessExit]", $"{nameof(App)}");
            Debug.WriteLine($"DomainID: {ad.Id}", $"{nameof(App)}");
            Debug.WriteLine($"FriendlyName: {ad.FriendlyName}", $"{nameof(App)}");
            Debug.WriteLine($"BaseDirectory: {ad.BaseDirectory}", $"{nameof(App)}");
        }
    }

    void DebugOnXamlResourceReferenceFailed(DebugSettings sender, XamlResourceReferenceFailedEventArgs args)
    {
        Debug.WriteLine($"[WARNING] XamlResourceReferenceFailed: {args.Message}");
        DebugLog($"OnXamlResourceReferenceFailed: {args.Message}");
    }

    void DebugOnBindingFailed(object sender, BindingFailedEventArgs args)
    {
        Debug.WriteLine($"[WARNING] BindingFailed: {args.Message}");
        DebugLog($"OnBindingFailed: {args.Message}");
    }

    /// <summary>
    /// Simplified debug logger for app-wide use.
    /// </summary>
    /// <param name="message">the text to append to the file</param>
    public static void DebugLog(string message)
    {
        try
        {
            if (App.IsPackaged)
                System.IO.File.AppendAllText(System.IO.Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "Debug.log"), $"[{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss.fff tt")}] {message}{Environment.NewLine}");
            else
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "Debug.log"), $"[{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss.fff tt")}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            Debug.WriteLine($"[{DateTime.Now.ToString("yyyy-MM-dd hh:mm:ss.fff tt")}] {message}");
        }
    }
    #endregion

    #region [Activation]
    void AnalyzeActivationData(object? data)
    {
        if (data == null)
            return;

        if (data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs laea)
        {
            if (laea.PreviousExecutionState != ApplicationExecutionState.ClosedByUser || laea.PreviousExecutionState != ApplicationExecutionState.NotRunning)
                DebugLog($"The app closed normally. PreviousState = {laea.PreviousExecutionState}");
            else
                DebugLog($"The app did not close normally. PreviousState = {laea.PreviousExecutionState}");
        }
        else if (data is Windows.ApplicationModel.Activation.IActivatedEventArgs aea)
        {
            if (aea.PreviousExecutionState != ApplicationExecutionState.ClosedByUser || aea.PreviousExecutionState != ApplicationExecutionState.NotRunning)
                DebugLog($"The app closed normally. PreviousState = {aea.PreviousExecutionState}");
            else
                DebugLog($"The app did not close normally. PreviousState = {aea.PreviousExecutionState}");
        }

        // Superfluous
        if (data is Windows.ApplicationModel.Activation.IActivatedEventArgsWithUser aeawu)
            DebugLog($"[INFO] IActivatedEventArgsWithUser: {aeawu.User.Type}");
        if (data is Windows.ApplicationModel.Activation.IDeviceActivatedEventArgs daea)
            DebugLog($"[INFO] IDeviceActivatedEventArgs: {daea.DeviceInformationId}");
        if (data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs faea)
            DebugLog($"[INFO] IFileActivatedEventArgs: {faea.Files.Count}");
        if (data is Windows.ApplicationModel.Activation.ISearchActivatedEventArgs saea)
            DebugLog($"[INFO] ISearchActivatedEventArgs: {saea.QueryText}");
        if (data is Windows.ApplicationModel.Activation.IToastNotificationActivatedEventArgs tnaea)
            DebugLog($"[INFO] IToastNotificationActivatedEventArgs: {tnaea.UserInput.Count}");
        if (data is Windows.ApplicationModel.Activation.IBackgroundActivatedEventArgs baea)
            DebugLog($"[INFO] IBackgroundActivatedEventArgs: {baea.TaskInstance.SuspendedCount}");
        if (data is Windows.ApplicationModel.Activation.IStartupTaskActivatedEventArgs staea)
            DebugLog($"[INFO] IStartupTaskActivatedEventArgs: {staea.TaskId}");
        if (data is Windows.ApplicationModel.Activation.IApplicationViewActivatedEventArgs avaea)
            DebugLog($"[INFO] IApplicationViewActivatedEventArgs: {avaea.CurrentlyShownApplicationViewId}");
    }

    void InstanceOnRedirectedActivated(object? sender, AppActivationArguments e)
    {
        DebugLog("App instance redirect activated.");
        if (m_window != null)
        {
            DebugLog("Calling MainWindow.Activate()");
            m_window.Activate();
        }
    }
    #endregion
}
