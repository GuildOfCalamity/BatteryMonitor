using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Windows.Devices.Power;

namespace BatteryMonitor;

public sealed partial class MainPage : Page, INotifyPropertyChanged
{
    #region [Props]
    readonly DispatcherQueue _localDispatcher;
    readonly DispatcherQueueSynchronizationContext? _syncContext;
    DispatcherTimer? _tmrUpdate;
    
    Windows.Devices.Power.Battery? _battery;
    Windows.System.Power.BatteryStatus _lastStatus = Windows.System.Power.BatteryStatus.NotPresent;

    public event PropertyChangedEventHandler? PropertyChanged;
    public void NotifyPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string propertyName = "")
    {
        if (string.IsNullOrEmpty(propertyName)) { return; }
        // Confirm that we're on the UI thread in the event that DependencyProperty is changed under forked thread.
        DispatcherQueue.InvokeOnUI(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));
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

    double _outlineWidth = 500;
    public double OutlineWidth
    {
        get => _outlineWidth;
        set { _outlineWidth = value; NotifyPropertyChanged(nameof(OutlineWidth)); }
    }

    double _outlineHeight = 80;
    public double OutlineHeight
    {
        get => _outlineHeight;
        set { _outlineHeight = value; NotifyPropertyChanged(nameof(OutlineHeight)); }
    }

    double _fillWidth = 450;
    public double FillWidth
    {
        get => _fillWidth;
        set { _fillWidth = value; NotifyPropertyChanged(nameof(FillWidth)); }
    }

    double _fillHeight = 78;
    public double FillHeight
    {
        get => _fillHeight;
        set { _fillHeight = value; NotifyPropertyChanged(nameof(FillHeight)); }
    }
    #endregion

    public MainPage()
    {
        this.InitializeComponent();
        this.Loaded += MainPageOnLoaded;
        this.Unloaded += MainPageOnUnloaded;
        _localDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _syncContext = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(_localDispatcher);
        SynchronizationContext.SetSynchronizationContext(_syncContext);
    }

    void MainPageOnUnloaded(object sender, RoutedEventArgs e)
    {
        ToggleTimer(false);
    }

    void MainPageOnLoaded(object sender, RoutedEventArgs e)
    {
        _battery = Windows.Devices.Power.Battery.AggregateBattery;
        ToggleTimer(true);
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

            // If the battery status has changed then bring our window to the foreground.
            if (_lastStatus != batteryReport.Status)
            {
                _lastStatus = batteryReport.Status;
            }

            if (batteryReport.Status == Windows.System.Power.BatteryStatus.NotPresent)
            {
                Charge = "Not Present";
                Remain = "⌛ Not Present";
                DrawBattery(25000, 50000, App.Profile != null ? App.Profile.windowWidth - 103 : 300);
                return;
            }

            //Debug.WriteLine($" Status............: {_lastStatus}         ");
            //Debug.WriteLine($" ChargeRate........: {Extensions.FormatMilliwatts(batteryReport.ChargeRateInMilliwatts)}        ");
            //Debug.WriteLine($" DesignCapacity....: {Extensions.FormatMilliwatts(batteryReport.DesignCapacityInMilliwattHours)}h      ");
            //Debug.WriteLine($" FullChargeCapacity: {Extensions.FormatMilliwatts(batteryReport.FullChargeCapacityInMilliwattHours)}h        ");
            //Debug.WriteLine($" RemainingCapacity.: {Extensions.FormatMilliwatts(batteryReport.RemainingCapacityInMilliwattHours)}h        ");

            if (batteryReport.ChargeRateInMilliwatts != null && batteryReport.ChargeRateInMilliwatts < 0)
            {
                Remain = $"⌛ {Extensions.MilliwattHoursToMinutes(batteryReport.RemainingCapacityInMilliwattHours, batteryReport.ChargeRateInMilliwatts)}";
                if (App.Profile != null && batteryReport.ChargeRateInMilliwatts != null)
                    App.Profile.lastRate = Math.Abs((int)batteryReport.ChargeRateInMilliwatts);
            }
            else if (App.Profile != null && App.Profile.lastRate > 0) // show a time based on the last power drain
            {
                Remain = $"⌛ {Extensions.MilliwattHoursToMinutes(batteryReport.RemainingCapacityInMilliwattHours, App.Profile.lastRate)}";
            }

            DrawBattery(batteryReport.RemainingCapacityInMilliwattHours, batteryReport.FullChargeCapacityInMilliwattHours, App.Profile != null ? App.Profile.windowWidth - 103 : 300);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateBatteryStatus: {ex.Message}");
        }
    }

    /// <summary>
    /// Calculates the bar fill and charge rate for our <see cref="Windows.Devices.Power.Battery.AggregateBattery"/>.
    /// </summary>
    void DrawBattery(int? currentValue, int? maxValue, int fullLength)
    {
        if (currentValue == null || maxValue == null)
            return;

        // Calculate the percentage of charge
        int percentage = (int)Math.Round(((double)currentValue.Value / maxValue.Value) * 100);

        // Calculate the number of battery characters to draw
        int barLength = (int)Math.Round(((double)percentage / 100) * fullLength);

        Charge = $"⚡ {percentage}%";

        //OutlineHeight = fullLength;
        //OutlineWidth = fullLength / 4.0;
        
        FillHeight = 70;
        FillWidth = barLength;
        
        //Debug.WriteLine($"[INFO] Rectangle fill width is {rectFill.ActualWidth} ");
    }
}
