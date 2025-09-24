using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using InstruMental.EventBroadcast;
using InstruMental.EventBroadcast.Messaging;

namespace InstruMental.Avalonia;

/// <summary>
/// Listens to published System.Diagnostics.Metrics instruments (focusing on Avalonia / .NET runtime metrics)
/// and emits them as instantaneous timeline events over the <see cref="TimelineEventBroadcaster"/>.
/// </summary>
public sealed class AvaloniaMetricsInstrumentor : IAsyncDisposable, IDisposable
{
    private readonly MeterListener _listener;
    private readonly TimelineEventBroadcaster _broadcaster;
    private readonly Func<Instrument, bool> _filter;
    private readonly TimeSpan _minDuration; // non-zero duration so UI consumers can render the dot/marker
    private readonly TimeSpan _pollInterval; // interval to call RecordObservableInstruments
    private bool _disposed;
    private const string AvaloniaDiagnosticMeterName = "Avalonia.Diagnostic.Meter";

    private readonly record struct MetricDescriptor(string Track, string Label, string ColorHex);

    // Only these metrics will be subscribed to / enabled.
    private static readonly IReadOnlyDictionary<string, MetricDescriptor> s_avaloniaDiagnosticMetrics =
        new Dictionary<string, MetricDescriptor>(StringComparer.Ordinal)
        {
            { "avalonia.comp.render.time", new MetricDescriptor("Metrics: Compositor", "Compositor Render", "#9370DB") },
            { "avalonia.comp.update.time", new MetricDescriptor("Metrics: Compositor", "Compositor Update", "#7B68EE") },
            { "avalonia.ui.measure.time", new MetricDescriptor("Metrics: Layout", "Layout Measure", "#2E8B57") },
            { "avalonia.ui.arrange.time", new MetricDescriptor("Metrics: Layout", "Layout Arrange", "#228B22") },
            { "avalonia.ui.render.time", new MetricDescriptor("Metrics: Layout", "Layout Render", "#008080") },
            { "avalonia.ui.input.time", new MetricDescriptor("Metrics: Input", "Input Processing", "#FF8C00") }
        };

    /// <param name="broadcaster">The broadcaster to push metric events to.</param>
    /// <param name="instrumentNamePrefixes">(Ignored for now except that they must still point at the Avalonia diagnostic meter). Optional prefixes (e.g. "Avalonia", "System.Runtime") to limit which meters are enabled. Null/empty = all.</param>
    /// <param name="minDuration">Artificial minimum duration to set for instantaneous metrics (default 100 microseconds).</param>
    /// <param name="pollInterval">Polling interval for observable instruments (default 1 second).</param>
    public AvaloniaMetricsInstrumentor(
        TimelineEventBroadcaster broadcaster,
        IReadOnlyList<string>? instrumentNamePrefixes = null,
        TimeSpan? minDuration = null,
        TimeSpan? pollInterval = null)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        var prefixes = instrumentNamePrefixes is { Count: > 0 } ? instrumentNamePrefixes : null;
        _minDuration = minDuration is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromMilliseconds(0.1); // 0.1ms minimal
        _pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : TimeSpan.FromSeconds(1); // default 1s

        // Filter so that ONLY the explicit Avalonia diagnostic timing metrics (names above) from the Avalonia diagnostic meter are enabled.
        _filter = inst =>
        {
            if (!string.Equals(inst.Meter.Name, AvaloniaDiagnosticMeterName, StringComparison.Ordinal))
                return false;
            if (!s_avaloniaDiagnosticMetrics.ContainsKey(inst.Name))
                return false; // not one of the desired instruments
            if (prefixes == null)
                return true; // prefixes not restricting
            foreach (var pfx in prefixes)
            {
                if (inst.Meter.Name.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        };

        _listener = new MeterListener();
        var listener = _listener; // capture local to avoid nullability warnings in lambda
        listener.InstrumentPublished = (instrument, state) =>
        {
            // Only enable measurements for the desired instruments; ignore everything else (including counters or other .NET runtime metrics).
            if (_filter(instrument))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };

        // Measurement callbacks (we still register broadly; only enabled instruments will produce events).
        listener.SetMeasurementEventCallback<double>(OnMeasurement);
        listener.SetMeasurementEventCallback<float>((i, m, t, s) => OnMeasurement(i, m, t, s));
        listener.SetMeasurementEventCallback<int>((i, m, t, s) => OnMeasurement(i, m, t, s));
        listener.SetMeasurementEventCallback<long>((i, m, t, s) => OnMeasurement(i, m, t, s));
        listener.SetMeasurementEventCallback<short>((i, m, t, s) => OnMeasurement(i, m, t, s));
        listener.SetMeasurementEventCallback<byte>((i, m, t, s) => OnMeasurement(i, m, t, s));
        listener.SetMeasurementEventCallback<decimal>((i, m, t, s) => OnMeasurement(i, (double)m, t, s));

        listener.Start();
    }

    private void OnMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        double value;
        try
        {
            value = Convert.ToDouble(measurement);
        }
        catch
        {
            return; // unsupported numeric conversion
        }

        var endTimestamp = HighResolutionClock.UtcNow;
        if (string.Equals(instrument.Meter.Name, AvaloniaDiagnosticMeterName, StringComparison.Ordinal) &&
            s_avaloniaDiagnosticMetrics.TryGetValue(instrument.Name, out var descriptor))
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return;
            }

            if (value < 0)
            {
                value = 0;
            }
            else
            {
                var maxMs = TimeSpan.MaxValue.TotalMilliseconds;
                if (value > maxMs)
                {
                    value = maxMs;
                }
            }

            var duration = TimeSpan.FromMilliseconds(value);
            var startTimestamp = duration > TimeSpan.Zero ? endTimestamp - duration : endTimestamp;
            var label = descriptor.Label;
            if (duration > TimeSpan.Zero)
            {
                label += $" {FormatDuration(duration)}";
            }
            var tagSuffix = FormatTags(tags);
            if (!string.IsNullOrEmpty(tagSuffix))
            {
                label += " " + tagSuffix;
            }

            _broadcaster.EnqueueComplete(new TimelineEventComplete(
                Guid.NewGuid(),
                descriptor.Track,
                label,
                descriptor.ColorHex,
                startTimestamp,
                endTimestamp));
            return;
        }

        /* General metrics disabled: we intentionally ignore all other instruments now. */
    }

    private static string FormatLabel(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var unit = instrument.Unit;
        string valStr;
        if (double.IsNaN(value) || double.IsInfinity(value))
            valStr = value.ToString(CultureInfo.InvariantCulture);
        else
            valStr = value.ToString("G5", CultureInfo.InvariantCulture);

        var label = unit is { Length: > 0 } ? $"{instrument.Name}={valStr}{unit}" : $"{instrument.Name}={valStr}";
        var tagSuffix = FormatTags(tags);
        if (!string.IsNullOrEmpty(tagSuffix))
        {
            label += " " + tagSuffix;
        }
        return label;
    }

    private static string FormatTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (tags.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(tags.Length);
        foreach (ref readonly var kv in tags)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
            {
                continue;
            }

            parts.Add($"{kv.Key}={kv.Value}");
        }

        return parts.Count > 0 ? "[" + string.Join(' ', parts) + "]" : string.Empty;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds >= 1)
        {
            return $"{duration.TotalMilliseconds:F3}ms";
        }

        var microseconds = duration.TotalMilliseconds * 1000.0;
        if (microseconds >= 1)
        {
            return $"{microseconds:F1}us";
        }

        var nanoseconds = duration.Ticks * 100; // 1 tick = 100ns
        return $"{nanoseconds}ns";
    }

    private static string ColorForInstrument(Instrument instrument)
    {
        // Deterministic pseudo-color based on instrument name hash to keep variety but stable across sessions.
        unchecked
        {
            int hash = instrument.Name.GetHashCode(StringComparison.OrdinalIgnoreCase);
            byte r = (byte)(hash & 0x7F | 0x80);
            byte g = (byte)((hash >> 7) & 0x7F | 0x80);
            byte b = (byte)((hash >> 14) & 0x7F | 0x80);
            return $"#{r:X2}{g:X2}{b:X2}";
        }
    }

    public Task RunAsync(CancellationToken ct)
    {
        // Keep task alive until cancellation. Metrics push via callbacks; we poll observable instruments.
        return Task.Run(async () =>
        {
            try
            {
                // Initial poll so that already-published observable instruments emit once quickly.
                _listener.RecordObservableInstruments();
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                    _listener.RecordObservableInstruments();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }, ct);
    }

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _listener.Dispose();
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        DisposeCore();
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }
}
