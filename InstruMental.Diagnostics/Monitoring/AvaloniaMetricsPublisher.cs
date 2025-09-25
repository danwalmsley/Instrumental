using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Threading;
using System.Formats.Cbor;
using InstruMental.Contracts.Monitoring;
using InstruMental.Contracts.Serialization;

namespace InstruMental.Diagnostics.Monitoring;

internal sealed class AvaloniaMetricsPublisher : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ActivityListener _activityListener;
    private const int ChannelCapacity = 32 * 1024;

    private readonly Channel<MonitoringEnvelope> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _senderTask;
    private readonly Timer _observableTimer;
    private readonly UdpClient _udpClient;
    private readonly TimeSpan _observableInterval;
    private readonly EnvelopeEncoding _encoding;
    private readonly TimeSpan _batchFlushInterval;
    private readonly int _maxBatchSize;
    private readonly int _maxDatagramSize;

    public AvaloniaMetricsPublisher(InstruMentalMonitoringOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _udpClient = new UdpClient();
        _udpClient.Connect(options.Host, options.Port);

        _observableInterval = options.ObservableInterval;
        _encoding = options.Encoding;
        _batchFlushInterval = options.BatchFlushInterval;
        _maxBatchSize = Math.Max(1, options.MaxBatchSize);
        _maxDatagramSize = Math.Max(1, options.MaxDatagramSize);
        _channel = Channel.CreateBounded<MonitoringEnvelope>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite
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
            ActivityStopped = OnActivityStopped,
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

        _senderTask = Task.Factory.StartNew(
            SendAsync,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default).Unwrap();
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
        var value = ConvertMeasurementToDouble(in measurement);
        Dictionary<string, string?>? tagDictionary = null;

        if (!tags.IsEmpty)
        {
            tagDictionary = new Dictionary<string, string?>(tags.Length, StringComparer.Ordinal);
            PopulateTags(tagDictionary, tags);
        }

        // If the instrument explicitly uses milliseconds and the instrument name indicates a duration (ends with ".time"),
        // interpret the measurement value as a duration in milliseconds and compute the event timestamp as "now - duration" using the
        // high-resolution clock. Otherwise, use the current UTC time.
        DateTimeOffset timestamp = HighResolutionClock.UtcNow;
        try
        {
            var unit = instrument.Unit;
            var name = instrument.Name;

            if (!string.IsNullOrEmpty(unit) && !string.IsNullOrEmpty(name) &&
                string.Equals(unit, "ms", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".time", StringComparison.Ordinal))
            {
                // HighResolutionClock.UtcNow provides a high-res DateTimeOffset. Subtract value (milliseconds).
                // Guard against NaN/Infinity and extremely large values.
                if (!double.IsNaN(value) && !double.IsInfinity(value))
                {
                    timestamp = HighResolutionClock.UtcNow - TimeSpan.FromMilliseconds(value);
                }
            }
        }
        catch
        {
            // If anything goes wrong computing timestamp from the value, fall back to UtcNow.
            timestamp = HighResolutionClock.UtcNow;
        }

        return new MetricSample
        {
            Timestamp = timestamp,
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

    private static void PopulateTags(Dictionary<string, string?> target, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        ref var start = ref MemoryMarshal.GetReference(tags);
        for (var i = 0; i < tags.Length; i++)
        {
            ref readonly var tag = ref Unsafe.Add(ref start, i);
            ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(target, tag.Key, out _);
            slot = tag.Value switch
            {
                null => null,
                string s => s,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => tag.Value.ToString()
            };
        }
    }

    private static double ConvertMeasurementToDouble<T>(in T measurement)
    {
        if (typeof(T) == typeof(double))
        {
            return Unsafe.As<T, double>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(float))
        {
            return Unsafe.As<T, float>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(decimal))
        {
            var dec = Unsafe.As<T, decimal>(ref Unsafe.AsRef(in measurement));
            return (double)dec;
        }

        if (typeof(T) == typeof(long))
        {
            return Unsafe.As<T, long>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(ulong))
        {
            return Unsafe.As<T, ulong>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(int))
        {
            return Unsafe.As<T, int>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(uint))
        {
            return Unsafe.As<T, uint>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(short))
        {
            return Unsafe.As<T, short>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(ushort))
        {
            return Unsafe.As<T, ushort>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(byte))
        {
            return Unsafe.As<T, byte>(ref Unsafe.AsRef(in measurement));
        }

        if (typeof(T) == typeof(sbyte))
        {
            return Unsafe.As<T, sbyte>(ref Unsafe.AsRef(in measurement));
        }

        return Convert.ToDouble(measurement, CultureInfo.InvariantCulture);
    }

    private async Task SendAsync()
    {
        try
        {
            var reader = _channel.Reader;
            var batch = new List<MonitoringEnvelope>(_maxBatchSize);

            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                // Pull any immediately-available items into the batch
                while (reader.TryRead(out var sample))
                {
                    batch.Add(sample);
                    if (batch.Count >= _maxBatchSize)
                    {
                        break;
                    }
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                // If batch is not yet full, wait a short interval to allow more items to accumulate
                if (batch.Count < _maxBatchSize)
                {
                    try
                    {
                        await Task.Delay(_batchFlushInterval, _cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // shutting down
                    }

                    // Drain any newly-arrived items up to the max batch size
                    while (batch.Count < _maxBatchSize && reader.TryRead(out var more))
                    {
                        batch.Add(more);
                    }
                }

                // Send the batch, packing pre-serialized envelopes into MTU-safe datagrams
                try
                {
                    // Pre-serialize each envelope exactly once
                    var serialized = new List<byte[]>(batch.Count);
                    serialized.Clear();
                    foreach (var env in batch)
                    {
                        try
                        {
                            serialized.Add(MonitoringEnvelopeSerializer.Serialize(env, _encoding));
                        }
                        catch
                        {
                            // Serialization failure for a single envelope: skip it
                        }
                    }

                    // Helper to get CBOR array header bytes for a given count
                    static byte[] GetCborArrayHeader(int count)
                    {
                        var w = new CborWriter();
                        w.WriteStartArray(count);
                        return w.Encode();
                    }

                    int idx = 0;
                    while (idx < serialized.Count)
                    {
                        // Build a datagram by greedily adding serialized items until adding next would exceed _maxDatagramSize
                        int start = idx;
                        long sumLen = 0;
                        int countInPacket = 0;

                        while (idx < serialized.Count)
                        {
                            var nextLen = serialized[idx].Length;
                            long projectedSize;

                            if (_encoding == EnvelopeEncoding.Json)
                            {
                                // JSON array: '[' + items joined by ',' + ']' => total = sumLen + nextLen + (countInPacket == 0 ? 2 : (countInPacket + 1))
                                // General formula: sumLen + nextLen + (countNew - 1) /*commas*/ + 2 /*brackets*/
                                // Here countNew = countInPacket + 1
                                projectedSize = sumLen + nextLen + (countInPacket) /*commas after adding this*/ + 2;
                            }
                            else
                            {
                                // CBOR array: header(len=countNew) + sumLen + nextLen
                                var header = GetCborArrayHeader(countInPacket + 1);
                                projectedSize = header.Length + sumLen + nextLen;
                            }

                            if (projectedSize <= _maxDatagramSize)
                            {
                                sumLen += nextLen;
                                countInPacket++;
                                idx++;
                                continue;
                            }

                            // If no items yet added and single item is too large, include it anyway (send oversized single envelope)
                            if (countInPacket == 0)
                            {
                                // We'll force a single-envelope packet
                                countInPacket = 1;
                                sumLen = nextLen;
                                idx++;
                            }

                            break;
                        }

                        // Build the datagram payload from serialized[start .. start+countInPacket-1]
                        if (countInPacket == 0)
                        {
                            // nothing to send (should not happen) - break to avoid infinite loop
                            break;
                        }

                        if (_encoding == EnvelopeEncoding.Json)
                        {
                            // Compute total size and allocate buffer
                            // total = sumLen + (countInPacket - 1) /*commas*/ + 2 /*brackets*/
                            var total = (int)(sumLen + (countInPacket - 1) + 2);
                            var payload = new byte[total];
                            int pos = 0;
                            payload[pos++] = (byte)'[';
                            for (int i = 0; i < countInPacket; i++)
                            {
                                var chunk = serialized[start + i];
                                Buffer.BlockCopy(chunk, 0, payload, pos, chunk.Length);
                                pos += chunk.Length;
                                if (i < countInPacket - 1)
                                {
                                    payload[pos++] = (byte)',';
                                }
                            }
                            payload[pos++] = (byte)']';

                            // Send
                            await _udpClient.SendAsync(payload, payload.Length).ConfigureAwait(false);
                        }
                        else
                        {
                            // CBOR: header + concatenated items
                            var header = GetCborArrayHeader(countInPacket);
                            var total = header.Length + (int)sumLen;
                            var payload = new byte[total];
                            int pos = 0;
                            Buffer.BlockCopy(header, 0, payload, pos, header.Length);
                            pos += header.Length;
                            for (int i = 0; i < countInPacket; i++)
                            {
                                var chunk = serialized[start + i];
                                Buffer.BlockCopy(chunk, 0, payload, pos, chunk.Length);
                                pos += chunk.Length;
                            }

                            await _udpClient.SendAsync(payload, payload.Length).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Ignore send failures and continue
                }
                finally
                {
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
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

        var startTime = HighResolutionClock.UtcNow - TimeSpan.FromMilliseconds(activity.Duration.TotalMilliseconds);

        var sample = new ActivitySample
        {
            Name = activity.DisplayName,
            StartTimestamp = startTime,
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
