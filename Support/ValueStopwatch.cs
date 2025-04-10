using System;
using System.Collections.Generic;

namespace BatteryMonitor;

/// <summary>
///   A memory efficient version of the <see cref="System.Diagnostics.Stopwatch"/>.
///   Because this timer's function is passive, there's no need/way for a
///   stop method. A reset method would be equivalent to calling StartNew().
/// </summary>
/// <remarks>
///   Structs are value types. This means they directly hold their data, 
///   unlike reference types (e.g. classes) that hold references to objects.
///   Value types cannot be null, they'll always have a value, even if it's 
///   the default value for their member data type(s). While you can't assign 
///   null directly to a struct, you can have struct members that are reference 
///   types (e.g. String), and those members can be null.
/// </remarks>
internal struct ValueStopwatch
{
    long _startTimestamp;
    // Set the ratio of timespan ticks to stopwatch ticks.
    static readonly double TimestampToTicks = TimeSpan.TicksPerSecond / (double)System.Diagnostics.Stopwatch.Frequency;
    public bool IsActive => _startTimestamp != 0;
    private ValueStopwatch(long startTimestamp) => _startTimestamp = startTimestamp;
    public static ValueStopwatch StartNew() => new ValueStopwatch(System.Diagnostics.Stopwatch.GetTimestamp());
    public TimeSpan GetElapsedTime()
    {
        // _startTimestamp cannot be zero for an initialized ValueStopwatch.
        if (!IsActive)
            throw new InvalidOperationException($"ValueStopwatch is uninitialized. Initialize the ValueStopwatch before using.");

        long end = System.Diagnostics.Stopwatch.GetTimestamp();
        long timestampDelta = end - _startTimestamp;
        long ticks = (long)(TimestampToTicks * timestampDelta);
        return new TimeSpan(ticks);
    }

    public string GetElapsedFriendly()
    {
        return ToHumanFriendly(GetElapsedTime());
    }

    #region [Helpers]
    string ToHumanFriendly(TimeSpan timeSpan)
    {
        if (timeSpan == TimeSpan.Zero)
            return "0 seconds";

        bool isNegative = false;
        List<string> parts = new();

        // Check for negative TimeSpan.
        if (timeSpan < TimeSpan.Zero)
        {
            isNegative = true;
            timeSpan = timeSpan.Negate(); // Make it positive for the calculations.
        }

        if (timeSpan.Days > 0)
            parts.Add($"{timeSpan.Days} day{(timeSpan.Days > 1 ? "s" : "")}");
        if (timeSpan.Hours > 0)
            parts.Add($"{timeSpan.Hours} hour{(timeSpan.Hours > 1 ? "s" : "")}");
        if (timeSpan.Minutes > 0)
            parts.Add($"{timeSpan.Minutes} minute{(timeSpan.Minutes > 1 ? "s" : "")}");
        if (timeSpan.Seconds > 0)
            parts.Add($"{timeSpan.Seconds} second{(timeSpan.Seconds > 1 ? "s" : "")}");

        // If no large amounts so far, try milliseconds.
        if (parts.Count == 0 && timeSpan.Milliseconds > 0)
            parts.Add($"{timeSpan.Milliseconds} millisecond{(timeSpan.Milliseconds > 1 ? "s" : "")}");

        // If no milliseconds, use ticks (nanoseconds).
        if (parts.Count == 0 && timeSpan.Ticks > 0)
        {
            // A tick is equal to 100 nanoseconds. While this maps well into units of time
            // such as hours and days, any periods longer than that aren't representable in
            // a succinct fashion, e.g. a month can be between 28 and 31 days, while a year
            // can contain 365 or 366 days. A decade can have between 1 and 3 leap-years,
            // depending on when you map the TimeSpan into the calendar. This is why TimeSpan
            // does not provide a "Years" property or a "Months" property.
            parts.Add($"{(timeSpan.Ticks * TimeSpan.TicksPerMicrosecond)} microsecond{((timeSpan.Ticks * TimeSpan.TicksPerMicrosecond) > 1 ? "s" : "")}");
        }

        // Join the sections with commas and "and" for the last one.
        if (parts.Count == 1)
            return isNegative ? $"Negative {parts[0]}" : parts[0];
        else if (parts.Count == 2)
            return isNegative ? $"Negative {string.Join(" and ", parts)}" : string.Join(" and ", parts);
        else
        {
            string lastPart = parts[parts.Count - 1];
            parts.RemoveAt(parts.Count - 1);
            return isNegative ? $"Negative " + string.Join(", ", parts) + " and " + lastPart : string.Join(", ", parts) + " and " + lastPart;
        }
    }
    #endregion
}
