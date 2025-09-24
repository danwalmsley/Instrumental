using System;
using System.Diagnostics;

namespace Diagnostics.Avalonia;

/// <summary>
/// Provides a shared high-resolution clock so different instrumentors can align timestamps.
/// </summary>
internal static class HighResolutionClock
{
    private static readonly long s_originTimestamp;
    private static readonly DateTimeOffset s_originUtc;
    private static readonly double s_timestampToTicks;

    static HighResolutionClock()
    {
        // Capture origin points once so all conversions share the same base.
        s_originTimestamp = Stopwatch.GetTimestamp();
        s_originUtc = DateTimeOffset.UtcNow;
        s_timestampToTicks = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;
    }

    public static long GetTimestamp() => Stopwatch.GetTimestamp();

    public static DateTimeOffset UtcNow => ToUtc(GetTimestamp());

    public static DateTimeOffset ToUtc(long timestamp)
    {
        var deltaTicks = (long)((timestamp - s_originTimestamp) * s_timestampToTicks);
        return s_originUtc + TimeSpan.FromTicks(deltaTicks);
    }

    public static TimeSpan TimestampDeltaToTimeSpan(long timestampDelta)
    {
        if (timestampDelta <= 0)
        {
            return TimeSpan.Zero;
        }

        var ticks = (long)(timestampDelta * s_timestampToTicks);
        return TimeSpan.FromTicks(ticks);
    }

    public static TimeSpan ElapsedSince(long startTimestamp)
        => TimestampDeltaToTimeSpan(GetTimestamp() - startTimestamp);
}
