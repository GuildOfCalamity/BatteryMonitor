//#define HAS_POWERGRID

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using Windows.Devices.Power;
using Windows.Storage.Streams;

namespace BatteryMonitor;

public sealed partial class MainPage : Page, INotifyPropertyChanged
{
    #region [Properties]
    ValueStopwatch _watch { get; set; }
    List<string> _backgrounds = new List<string>();
    readonly DispatcherQueue _localDispatcher;
    readonly DispatcherQueueSynchronizationContext? _syncContext;
    DispatcherTimer? _tmrUpdate;
    Windows.Devices.Power.Battery? _battery;

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        if (string.IsNullOrEmpty(propertyName)) { return; }
        // Confirm that we're on the UI thread in the event that DependencyProperty is changed under forked thread.
        DispatcherQueue.InvokeOnUI(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
    }

    Windows.System.Power.BatteryStatus _lastStatus = Windows.System.Power.BatteryStatus.NotPresent;
    public Windows.System.Power.BatteryStatus LastStatus
    {
        get => _lastStatus;
        set { _lastStatus = value; NotifyPropertyChanged(nameof(LastStatus)); }
    }

    bool _isBusy = false;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; NotifyPropertyChanged(nameof(IsBusy)); }
    }

    string _charge = "0%";
    public string Charge
    {
        get => _charge;
        set { _charge = value; NotifyPropertyChanged(nameof(Charge)); }
    }

    string _remain = "1 hour";
    public string Remain
    {
        get => _remain;
        set { _remain = value; NotifyPropertyChanged(nameof(Remain)); }
    }

    double _outlineWidth = 322;
    public double OutlineWidth
    {
        get => _outlineWidth;
        set { _outlineWidth = value; NotifyPropertyChanged(nameof(OutlineWidth)); }
    }

    double _outlineHeight = 78;
    public double OutlineHeight
    {
        get => _outlineHeight;
        set { _outlineHeight = value; NotifyPropertyChanged(nameof(OutlineHeight)); }
    }

    double _fillWidth = 320;
    public double FillWidth
    {
        get => _fillWidth;
        set { _fillWidth = value; NotifyPropertyChanged(nameof(FillWidth)); }
    }

    double _fillHeight = 76;
    public double FillHeight
    {
        get => _fillHeight;
        set { _fillHeight = value; NotifyPropertyChanged(nameof(FillHeight)); }
    }

    double _cornerRadius = 6;
    public double CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = value; NotifyPropertyChanged(nameof(CornerRadius)); }
    }

    Brush _fillBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
    public Brush FillBrush
    {
        get => _fillBrush;
        set { _fillBrush = value; NotifyPropertyChanged(nameof(FillBrush)); }
    }

    int _workWidth = 100;
    LinearGradientBrush? brush100;
    LinearGradientBrush? brush75;
    LinearGradientBrush? brush50;
    LinearGradientBrush? brush25;
    #endregion

    public MainPage()
    {
        _watch = ValueStopwatch.StartNew();
        this.InitializeComponent();
        this.Loaded += MainPageOnLoaded;
        this.Unloaded += MainPageOnUnloaded;

        _localDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _syncContext = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(_localDispatcher);
        SynchronizationContext.SetSynchronizationContext(_syncContext);

        #region [Brush Setup]
        // Color.FromArgb(255, 255, 251, 100); //  Yellow
        // Color.FromArgb(255, 255, 229, 005); //  |
        // Color.FromArgb(255, 255, 201, 005); //  |
        // Color.FromArgb(255, 255, 184, 005); //  |
        // Color.FromArgb(255, 255, 165, 000); //  Orange
        // Color.FromArgb(255, 242, 139, 011); //  |
        // Color.FromArgb(255, 236, 102, 011); //  |
        // Color.FromArgb(255, 244, 086, 017); //  |
        // Color.FromArgb(255, 255, 039, 017); //  ▼
        // Color.FromArgb(255, 255, 010, 005); //  Red

        brush100 = Extensions.CreateDiagonalGradientBrush(
            Windows.UI.Color.FromArgb(225, 255, 160, 0), // Orange
            Windows.UI.Color.FromArgb(225, 240, 226, 0), // Yellow
            Windows.UI.Color.FromArgb(225, 20, 255, 0)); // Green

        brush75 = Extensions.CreateDiagonalGradientBrush(
            Windows.UI.Color.FromArgb(225, 255, 120, 0),  // Red-Orange
            Windows.UI.Color.FromArgb(225, 255, 200, 0),  // Orange-Yellow
            Windows.UI.Color.FromArgb(225, 155, 255, 0)); // Green-Yellow

        brush50 = Extensions.CreateDiagonalGradientBrush(
            Windows.UI.Color.FromArgb(225, 255, 50, 0),   // Red
            Windows.UI.Color.FromArgb(225, 255, 160, 0),  // Orange
            Windows.UI.Color.FromArgb(225, 240, 226, 0)); // Yellow

        brush25 = Extensions.CreateDiagonalGradientBrush(
            Windows.UI.Color.FromArgb(225, 255, 50, 0),   // Red-Orange
            Windows.UI.Color.FromArgb(225, 255, 100, 0),  // Orange
            Windows.UI.Color.FromArgb(225, 255, 150, 0)); // Yellow-Orange
        #endregion

        // PowerGrid is not accessible in WinUI3/UWP apps targeting desktop. It's
        // only supported on certain device families (e.g., IoT, Surface Hub, Xbox).
        if (Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Devices.Power.PowerGridForecast"))
            Debug.WriteLine("PowerGridForcast is available");
        else
            Debug.WriteLine("PowerGridForcast is not available");
    }

    void MainPageOnLoaded(object sender, RoutedEventArgs e)
    {
        IsBusy = true;
        //var subclasses = Extensions.GetDerivedClasses(sender);
        _workWidth = App.Profile != null ? App.Profile.windowWidth - 141 : 300;
        _battery = Windows.Devices.Power.Battery.AggregateBattery;
        ToggleTimer(true);
        //FillBrush = Extensions.CreateDiagonalGradientBrush(Windows.UI.Color.FromArgb(255, 255, 50, 0), Windows.UI.Color.FromArgb(255, 255, 160, 0), Windows.UI.Color.FromArgb(255, 240, 226, 0), Windows.UI.Color.FromArgb(255, 20, 255, 0));
        if (App.Profile != null && App.Profile.logging)
            Logger.SetLoggerFolderPath(AppDomain.CurrentDomain.BaseDirectory);

        if (App.Profile != null && App.Profile.transparency)
            imgBattery.Opacity = 0.75d;
        else
            imgBattery.Opacity = 0.85d;

        #region [Asset Load]
        //var images = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets")
        // We could just directly set the user's background image name to the control, but this async method is fun to use.
        System.IO.Path.Combine(AppContext.BaseDirectory, "Assets").EnumerateFilesWithProgressAsync(searchPattern: "*.png", recursive: false, App.CoreToken != null ? App.CoreToken.Token : CancellationToken.None,
            (s, e) => {
                Debug.WriteLine($"• Found '{e.UserState}' ({e.ProgressPercentage})");
            }).ContinueWith((t) => {
                if (t.IsFaulted)
                    Debug.WriteLine($"⇒ EnumerateFiles(Faulted): {t.Exception?.GetBaseException().Message}");
                else {
                    _backgrounds = t.Result;
                    if (App.Profile != null && !string.IsNullOrEmpty(App.Profile.backgroundImage))
                    {
                        var match = _backgrounds.Where(x => Regex.IsMatch(x, App.Profile.backgroundImage)).FirstOrDefault();
                        if (!string.IsNullOrEmpty(match))
                        {
                            _localDispatcher.TryEnqueue(() => { imgBattery.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(match)); });
                        }
                    }
                }
            }).ConfigureAwait(false);
        #endregion
    }

    void MainPageOnUnloaded(object sender, RoutedEventArgs e)
    {
        ToggleTimer(false);
        if (App.Profile != null && App.Profile.logging)
        {
            Logger.Log($"⏱️ Application instance ran for {_watch.GetElapsedFriendly()}", true);
            Logger.ConfirmLogIsFlushed(2000);
        }
    }

    void ToggleTimer(bool enabled)
    {
        if (enabled)
        {
            if (_tmrUpdate == null)
            {
                _tmrUpdate = new DispatcherTimer();
                _tmrUpdate.Interval = TimeSpan.FromMilliseconds(App.Profile != null ? App.Profile.refresh : 3000);
                _tmrUpdate.Tick += UpdateTimerOnTick;
                _tmrUpdate.Start();
            }
            else
                _tmrUpdate?.Stop();
        }
        else
        {
            if (_tmrUpdate != null)
            {
                _tmrUpdate.Tick -= UpdateTimerOnTick;
                _tmrUpdate?.Stop();
                _tmrUpdate = null;
            }
        }
    }

    void UpdateTimerOnTick(object? sender, object e)
    {
        if (App.IsClosing)
            return;

        UpdateBatteryStats(_battery);
    }

    void UpdateBatteryStats(Battery? battery)
    {
        if (battery is null)
            return;

        try
        {
            BatteryReport batteryReport = battery.GetReport();

            if (LastStatus != batteryReport.Status)
            {
                LastStatus = batteryReport.Status;
                // If the battery status has changed then bring our window to the foreground.
                if (App.Profile != null && !App.Profile.topmost)
                    App.ActivateMainWindow();
            }

            if (batteryReport.Status == Windows.System.Power.BatteryStatus.NotPresent)
            {
                Charge = "Not Present";
                Remain = "⚠️ N/A (simulation)";
                if (tbStatus.Visibility == Visibility.Visible)
                    tbStatus.Visibility = Visibility.Collapsed;
                DrawBattery(Random.Shared.Next(1, 49000), 50000, _workWidth, true);
                return;
            }

            Debug.WriteLine($"┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄┄");
            Debug.WriteLine($"📝 ChargeRate........: {Extensions.FormatMilliwatts(batteryReport.ChargeRateInMilliwatts)}        ");
            Debug.WriteLine($"📝 DesignCapacity....: {Extensions.FormatMilliwatts(batteryReport.DesignCapacityInMilliwattHours)}h      ");
            Debug.WriteLine($"📝 FullChargeCapacity: {Extensions.FormatMilliwatts(batteryReport.FullChargeCapacityInMilliwattHours)}h        ");
            Debug.WriteLine($"📝 RemainingCapacity.: {Extensions.FormatMilliwatts(batteryReport.RemainingCapacityInMilliwattHours)}h        ");

            // Draining
            if (batteryReport.ChargeRateInMilliwatts != null && batteryReport.ChargeRateInMilliwatts < 0)
            {
                Remain = $"⌛ {Extensions.MilliwattHoursToMinutes(batteryReport.RemainingCapacityInMilliwattHours, batteryReport.ChargeRateInMilliwatts)}";
                if (App.Profile != null)
                    App.Profile.lastDrainRate = Math.Abs((int)batteryReport.ChargeRateInMilliwatts);
            }
            // Charging
            else if (batteryReport.ChargeRateInMilliwatts != null && batteryReport.ChargeRateInMilliwatts > 0)
            {
                if (App.Profile != null)
                {
                    App.Profile.lastChargeRate = Math.Abs((int)batteryReport.ChargeRateInMilliwatts);
                    Remain = $"⌛ {Extensions.MilliwattHoursToMinutes(batteryReport.RemainingCapacityInMilliwattHours, App.Profile.lastDrainRate)}";
                }
                else
                {
                    Remain = $"⌛ {Extensions.MilliwattHoursToMinutes(batteryReport.RemainingCapacityInMilliwattHours, batteryReport.ChargeRateInMilliwatts)}";
                }
            }
            // Idle
            else if (App.Profile != null) // show a time based on the last power drain
            {
                if (App.Profile.lastChargeRate > App.Profile.lastDrainRate)
                    Remain = $"⌛ {Extensions.MilliwattHoursToMinutes(batteryReport.RemainingCapacityInMilliwattHours, App.Profile.lastChargeRate)}";
                else
                    Remain = $"⌛ {Extensions.MilliwattHoursToMinutes(batteryReport.RemainingCapacityInMilliwattHours, App.Profile.lastDrainRate)}";
            }

            DrawBattery(batteryReport.RemainingCapacityInMilliwattHours, batteryReport.FullChargeCapacityInMilliwattHours, _workWidth, false);
        }
        catch (Exception ex)
        {
            Logger.Log(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Calculates the bar fill and charge rate for our <see cref="Windows.Devices.Power.Battery.AggregateBattery"/>.
    /// </summary>
    void DrawBattery(int? currentValue, int? maxValue, int fullLength, bool simulate)
    {
        if (currentValue == null || maxValue == null)
            return;

        try
        {
            // Calculate the percentage of charge
            int percentage = (int)Math.Round(((double)currentValue.Value / maxValue.Value) * 100);

            // Calculate the number of battery characters to draw
            int barLength = (int)Math.Round(((double)percentage / 100) * fullLength);

            Charge = $"⚡ {percentage}%";

            //OutlineHeight = 74;
            //OutlineWidth = fullLength;

            CornerRadius = App.Profile != null ? App.Profile.cornerRadius : 6;
            FillHeight = App.Profile != null ? App.Profile.fillHeight : 78;
            if (simulate)
                AnimateWidth(rectFill, FillWidth, barLength, 0.125d);
            else
                FillWidth = barLength;

            switch (percentage)
            {
                case int p when p >= 75: FillBrush = brush100!; break;
                case int p when p >= 50: FillBrush = brush75!; break;
                case int p when p >= 25: FillBrush = brush50!; break;
                case int p when p >= 0: FillBrush = brush25!; break;
            }

            // You could add an event here when the level drops too low to automatically shutdown the system.

        }
        catch (Exception ex)
        {
            Logger.Log(ex);
        }
    }

    void AnimateWidth(FrameworkElement element, double from, double to, double durationSeconds)
    {
        const int frameRate = 50;
        int totalFrames = (int)(durationSeconds * frameRate);
        int currentFrame = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / frameRate) };
        timer.Tick += (s, e) =>
        {
            if (App.IsClosing)
            {
                ((DispatcherTimer)s!).Stop();
                return;
            }
            currentFrame++;
            double t = (double)currentFrame / totalFrames;
            element.Width = Extensions.Lerp(from, to, t);
            if (currentFrame >= totalFrames)
            {
                element.Width = to;
                ((DispatcherTimer)s!).Stop();
                FillWidth = to;
            }
        };
        timer.Start();
    }

    async Task AnimateWidthAsync(FrameworkElement element, double from, double to, double durationSeconds)
    {
        const int frameRate = 50;
        int frames = (int)(durationSeconds * frameRate);
        double interval = durationSeconds / frames;
        for (int i = 1; i <= frames; i++)
        {
            if (App.IsClosing) { return; }
            double t = (double)i / frames;
            double width = Extensions.Lerp(from, to, t);
            element.Width = width;
            FillWidth = to;
            await Task.Delay(TimeSpan.FromSeconds(interval));
        }
        element.Width = to;
    }

    #region [Tests]
    public async void TestActionSequenceExtension(CancellationToken token)
    {
        List<Action> steps = new()
        {
            () => Debug.WriteLine("Step 1"),
            () => throw new InvalidOperationException("Simulated failure at step 2"),
            () => Debug.WriteLine("Step 3"),
        };

        ProgressChangedEventHandler progress = (s, e) =>
        {
            if (e.ProgressPercentage == -1)
                Debug.WriteLine($"[ERROR] {e.UserState}");
            else
                Debug.WriteLine($"✅ Action #{e.ProgressPercentage} completed");
        };

        Action<Exception> errorHandler = ex =>
        {
            Debug.WriteLine($"[EXCEPTION] {ex.Message}");
        };

        await Extensions.InvokeActionsWithProgressAsync(steps, errorHandler, token, progress);
    }

    public void StreamExtensionTest(string inFileName, string outFileName, CancellationToken token)
    {
        ulong currentPosition = 0;
        using (FileStream input = File.OpenRead(inFileName))
        {
            ulong TotalSize = Convert.ToUInt64(input.Length);
            using (FileStream output = File.Open(outFileName, FileMode.OpenOrCreate, FileAccess.Write))
            {
                input.CopyTo(output, Convert.ToInt64(input.Length), token, (s, e) =>
                {
                    Debug.WriteLine($" Progress: " + Convert.ToString(Math.Ceiling((currentPosition + Convert.ToUInt64(e.ProgressPercentage / 100d * input.Length)) * 100d / TotalSize)));
                });
            }
        }
    }

    public async Task InvokeOutputStreamWriteAsync(uint capacity = 256)
    {
        byte[] data = new byte[capacity];
        Random.Shared.NextBytes(data);

        using var stream = new InMemoryRandomAccessStream().AsStream();
        await stream.WriteAsync(data, 0, (int)capacity);
        stream.Position = 0; // reset position

        using var fileStream = File.OpenWrite(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OutputStream.txt"));
        using var winRTStream = fileStream.AsOutputStream();
        var winRTBuffer = new Windows.Storage.Streams.Buffer(capacity);

        StreamIOHelper.CopyByteArrayToBuffer(data, winRTBuffer);

        await winRTStream.WriteAsync(winRTBuffer);
        await winRTStream.FlushAsync();
        winRTStream.Dispose();
    }
    #endregion

#if HAS_POWERGRID
    void TestPowerGridClass()
    {
        Windows.Devices.Power.PowerGridForecast forecast = Windows.Devices.Power.PowerGridForecast.GetForecast();
        PrintBestTimes(forecast);
    }

    void PrintBestTimes(Windows.Devices.Power.PowerGridForecast forecast)
    {
        double bestSeverity = double.MaxValue;
        double bestLowImpactSeverity = double.MaxValue;
        DateTime bestTime = DateTime.MaxValue;
        DateTime bestLowImpactTime = DateTime.MaxValue;
        TimeSpan blockDuration = forecast.BlockDuration;
        DateTime startTime = forecast.StartTime;
        IList<Windows.Devices.Power.PowerGridData> forecastSignals = forecast.Forecast;

        if (forecastSignals.Count == 0)
        {
            Console.WriteLine("Error encountered with getting forecast; try again later.");
            return;
        }

        foreach (Windows.Devices.Power.PowerGridData data in forecastSignals)
        {
            if (data.Severity < bestSeverity)
            {
                bestSeverity = data.Severity;
                bestTime = startTime;
            }

            if (data.IsLowUserExperienceImpact && data.Severity < bestLowImpactSeverity)
            {
                bestLowImpactSeverity = data.Severity;
                bestLowImpactTime = startTime;
            }

            startTime = startTime + blockDuration;
        }

        if (bestLowImpactTime != DateTime.MaxValue)
        {
            DateTime endBestLowImpactTime = bestLowImpactTime + blockDuration;
            Console.WriteLine($"Lowest severity during low impact is {bestLowImpactSeverity}, which starts at {bestLowImpactTime.ToString()}, and ends at {endBestLowImpactTime}.");
        }
        else
        {
            Console.WriteLine("There's no low-user-impact time in which to do work.");
        }

        if (bestTime != DateTime.MaxValue)
        {
            DateTime endBestSeverity = bestTime + blockDuration;
            Console.WriteLine($"Lowest severity is {bestSeverity}, which starts at {bestTime.ToString()}, and ends at {endBestSeverity.ToString()}.");
        }
    }
#endif

}
