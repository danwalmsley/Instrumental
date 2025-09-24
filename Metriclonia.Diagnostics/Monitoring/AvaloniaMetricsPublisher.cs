using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using InstruMental.Avalonia;
using InstruMental.Contracts.Monitoring;
using InstruMental.Contracts.Serialization;

namespace InstruMental.Diagnostics.Monitoring;

internal sealed class AvaloniaMetricsPublisher : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ActivityListener _activityListener;
    private readonly Channel<MonitoringEnvelope> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _senderTask;
    private readonly Timer _observableTimer;
    private readonly UdpClient _udpClient;
    private readonly TimeSpan _observableInterval;
    private readonly EnvelopeEncoding _encoding;
    private readonly TimeSpan _batchFlushInterval;
    private readonly int _maxBatchSize;

    public AvaloniaMetricsPublisher(MetricloniaMonitoringOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _udpClient = new UdpClient();
        _udpClient.Connect(options.Host, options.Port);

        _observableInterval = options.ObservableInterval;
        _encoding = options.Encoding;
        _batchFlushInterval = options.BatchFlushInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(100) : options.BatchFlushInterval;
        _maxBatchSize = options.MaxBatchSize > 0 ? options.MaxBatchSize : 100;
        _channel = Channel.CreateUnbounded<MonitoringEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name.StartsWith("Avalonia", StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<decimal>(OnMeasurement);
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.SetMeasurementEventCallback<float>(OnMeasurement);
        _listener.SetMeasurementEventCallback<long>(OnMeasurement);
        _listener.SetMeasurementEventCallback<int>(OnMeasurement);
        _listener.SetMeasurementEventCallback<short>(OnMeasurement);
        _listener.SetMeasurementEventCallback<byte>(OnMeasurement);

        _listener.Start();

        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == DiagnosticSourceName,
            Sample = SampleAllData,
            SampleUsingParentId = SampleAllDataUsingParentId,
            ActivityStopped = OnActivityStopped
        };

        ActivitySource.AddActivityListener(_activityListener);

        _observableTimer = new Timer(_ =>
        {
            try
            {
                _listener.RecordObservableInstruments();
            }
            catch
            {
                // Swallow to avoid terminating timer loop; listener keeps working.
            }
        }, null, _observableInterval, _observableInterval);

        _senderTask = Task.Run(SendAsync);
    }

    private void OnMeasurement<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state)
    {
        if (!_channel.Writer.TryWrite(MonitoringEnvelope.FromMetric(CreateSample(instrument, measurement, tags))))
        {
            // Channel is full or completed; drop the measurement to avoid blocking instrument threads.
        }
    }

    private static MetricSample CreateSample<T>(Instrument instrument, T measurement, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        double value = measurement switch
        {
            double d => d,
            float f => f,
            decimal dec => (double)dec,
            long l => l,
            ulong ul => ul,
            int i => i,
            uint ui => ui,
            short s => s,
            ushort us => us,
            byte b => b,
            sbyte sb => sb,
            _ => Convert.ToDouble(measurement)
        };

        var tagDictionary = tags.Length == 0
            ? null
            : new Dictionary<string, string?>(tags.Length, StringComparer.Ordinal);

        if (tagDictionary is not null)
        {
            foreach (var tag in tags)
            {
                tagDictionary[tag.Key] = tag.Value?.ToString();
            }
        }

        return new MetricSample
        {
            Timestamp = ShouldUseDurationAsTimestamp(instrument, value, instrument.Unit) 
                ? HighResolutionClock.UtcNow - TimeSpan.FromMilliseconds(value)
                : HighResolutionClock.UtcNow,
            MeterName = instrument.Meter.Name,
            InstrumentName = instrument.Name,
            InstrumentType = instrument.GetType().Name,
            Unit = instrument.Unit,
            Description = instrument.Description,
            Value = value,
            ValueType = typeof(T).Name,
            Tags = tagDictionary
        };
    }

    private static bool ShouldUseDurationAsTimestamp(Instrument instrument, double value, string? unit)
    {
        if (string.IsNullOrEmpty(unit) || !unit.Equals("ms", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = instrument.Name ?? string.Empty;
        return name.EndsWith(".time", StringComparison.OrdinalIgnoreCase) && value >= 0 && double.IsFinite(value);
    }

    private async Task SendAsync()
    {
        var batch = new List<MonitoringEnvelope>(_maxBatchSize);
        var sw = Stopwatch.StartNew();
        try
        {
            var reader = _channel.Reader;
            while (!_cts.IsCancellationRequested)
            {
                // Pull as many as available up to max batch size
                while (batch.Count < _maxBatchSize && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                var shouldFlush = batch.Count > 0 && (batch.Count >= _maxBatchSize || sw.Elapsed >= _batchFlushInterval);

                if (shouldFlush)
                {
                    try
                    {
                        var payload = MonitoringEnvelopeSerializer.SerializeBatch(batch, _encoding);
                        await _udpClient.SendAsync(payload, payload.Length).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                        // Ignore send failures for individual batches.
                    }
                    finally
                    {
                        batch.Clear();
                        sw.Restart();
                    }
                }

                // If no data, wait for either incoming data or timeout for time-based flush
                if (batch.Count == 0)
                {
                    var waitTask = reader.WaitToReadAsync(_cts.Token).AsTask();
                    var delayTask = Task.Delay(_batchFlushInterval, _cts.Token);
                    await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        finally
        {
            // Final flush
            if (batch.Count > 0)
            {
                try
                {
                    var payload = MonitoringEnvelopeSerializer.SerializeBatch(batch, _encoding);
                    await _udpClient.SendAsync(payload, payload.Length).ConfigureAwait(false);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Dispose();
        _activityListener.Dispose();
        _observableTimer.Dispose();
        _channel.Writer.TryComplete();
        try
        {
            _senderTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Suppress dispose-time exceptions.
        }
        _udpClient.Dispose();
        _cts.Dispose();
    }

    private void OnActivityStopped(Activity activity)
    {
        if (activity.Source.Name != DiagnosticSourceName)
        {
            return;
        }

        var tags = activity.Tags;
        Dictionary<string, string?>? tagDictionary = null;

        if (tags is not null)
        {
            foreach (var tag in tags)
            {
                tagDictionary ??= new Dictionary<string, string?>(StringComparer.Ordinal);
                tagDictionary[tag.Key] = tag.Value;
            }
        }

        // Align timestamps to HighResolutionClock domain
        var nowUtc = HighResolutionClock.UtcNow;
        var startUtc = nowUtc - activity.Duration;

        var sample = new ActivitySample
        {
            Name = activity.DisplayName,
            StartTimestamp = startUtc,
            DurationMilliseconds = activity.Duration.TotalMilliseconds,
            Status = activity.Status.ToString(),
            StatusDescription = activity.StatusDescription,
            TraceId = activity.TraceId.ToString(),
            SpanId = activity.SpanId.ToString(),
            Tags = tagDictionary
        };

        if (!_channel.Writer.TryWrite(MonitoringEnvelope.FromActivity(sample)))
        {
            // Channel is full or completed; drop the activity to avoid blocking instrumentation.
        }
    }

    private const string DiagnosticSourceName = "Avalonia.Diagnostic.Source";

    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllData;

    private static ActivitySamplingResult SampleAllDataUsingParentId(ref ActivityCreationOptions<string> _)
        => ActivitySamplingResult.AllData;
}
