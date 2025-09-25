using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using InstruMental.Contracts.Monitoring;
using InstruMental.Contracts.Serialization;
using Microsoft.Extensions.Logging;

namespace InstruMental.Diagnostics.Services;

public sealed class UdpMetricsListener : IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly bool EnablePayloadTraceLogging = false;

    private readonly int _port;
    private readonly UdpClient _udpClient;
    private readonly EnvelopeEncoding? _preferredEncoding;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<UdpReceiveResult> _packets;
    private Task? _receiveTask;
    private Task[]? _processingTasks;
    private long _droppedPackets;
    private readonly ILogger? _logger;

    public UdpMetricsListener(int port, EnvelopeEncoding? preferredEncoding = null, IPAddress? listenAddress = null, ILogger? logger = null)
    {
        _port = port;
        _preferredEncoding = preferredEncoding;
        _logger = logger;

        _udpClient = new UdpClient(AddressFamily.InterNetwork);
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(listenAddress ?? IPAddress.Any, port));
        _logger?.LogInformation("UDP listener bound to port {Port} (preferred encoding {Encoding})", port, preferredEncoding?.ToString() ?? "auto-detect");

        _packets = Channel.CreateBounded<UdpReceiveResult>(new BoundedChannelOptions(4096)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
    }

    public event Action<MetricSample>? MetricReceived;

    public event Action<ActivitySample>? ActivityReceived;

    public void Start()
    {
        if (_receiveTask is null)
        {
            _logger?.LogInformation("Starting UDP receive loop on {Port}", _port);
            _receiveTask = Task.Factory.StartNew(
                ReceiveLoopAsync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();

            var workerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, Environment.ProcessorCount);
            _processingTasks = new Task[workerCount];
            for (var i = 0; i < workerCount; i++)
            {
                _processingTasks[i] = Task.Factory.StartNew(
                    ProcessLoopAsync,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap();
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udpClient.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogDebug("UDP receive canceled");
                    break;
                }

                if (!_packets.Writer.TryWrite(result))
                {
                    Interlocked.Increment(ref _droppedPackets);
                    if (EnablePayloadTraceLogging)
                    {
                        _logger?.LogTrace("Dropped UDP packet ({Length} bytes) due to full processing queue", result.Buffer.Length);
                    }
                }
            }
        }
        finally
        {
            _packets.Writer.TryComplete();
            _udpClient.Close();
            _logger?.LogInformation("UDP listener on port {Port} stopped ({Dropped} dropped packets)", _port, Volatile.Read(ref _droppedPackets));
        }
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            var reader = _packets.Reader;
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                while (reader.TryRead(out var packet))
                {
                    ProcessPacket(packet);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
    }

    private void ProcessPacket(in UdpReceiveResult result)
    {
        var payload = result.Buffer.AsMemory();

        try
        {
            var parsed = false;
            MonitoringEnvelope? envelope = null;

            if (_preferredEncoding.HasValue)
            {
                parsed = MonitoringEnvelopeSerializer.TryDeserialize(payload, _preferredEncoding.Value, out envelope);
            }

            if (!parsed)
            {
                parsed = MonitoringEnvelopeSerializer.TryDeserialize(payload, out envelope);
            }

            if (!parsed)
            {
                envelope = JsonSerializer.Deserialize<MonitoringEnvelope>(payload.Span, s_jsonOptions);
            }

            if (envelope is null)
            {
                _logger?.LogWarning("Received payload could not be deserialized ({Length} bytes)", payload.Length);
                return;
            }

            if (string.Equals(envelope.Type, EnvelopeTypes.Metric, StringComparison.OrdinalIgnoreCase) && envelope.Metric is not null)
            {
                DispatchMetric(envelope.Metric);
                return;
            }

            if (string.Equals(envelope.Type, EnvelopeTypes.Activity, StringComparison.OrdinalIgnoreCase) && envelope.Activity is not null)
            {
                DispatchActivity(envelope.Activity);
                return;
            }

            if (envelope.Metric is not null && string.IsNullOrEmpty(envelope.Type))
            {
                // Back-compat: metrics prior to envelope introduction.
                DispatchMetric(envelope.Metric);
                return;
            }

            if (TryHandleLegacyMetric(payload))
            {
                return;
            }

            _logger?.LogWarning("Received payload with unknown type '{Type}'", envelope.Type);
        }
        catch (JsonException ex)
        {
            if (TryHandleLegacyMetric(payload))
            {
                return;
            }

            _logger?.LogWarning(ex, "Failed to deserialize metric payload ({Length} bytes)", payload.Length);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Unhandled exception while parsing metric payload");
        }
    }

    private void DispatchMetric(MetricSample sample)
    {
        if (EnablePayloadTraceLogging)
        {
            _logger?.LogTrace("Received metric payload for {Meter}/{Instrument}", sample.MeterName, sample.InstrumentName);
        }
        MetricReceived?.Invoke(sample);
    }

    private void DispatchActivity(ActivitySample activity)
    {
        if (EnablePayloadTraceLogging)
        {
            _logger?.LogTrace("Received activity payload for {Name}", activity.Name);
        }
        ActivityReceived?.Invoke(activity);
    }

    private bool TryHandleLegacyMetric(ReadOnlyMemory<byte> buffer)
    {
        try
        {
            var legacyMetric = JsonSerializer.Deserialize<MetricSample>(buffer.Span, s_jsonOptions);
            if (legacyMetric is not null && legacyMetric.Timestamp != default)
            {
                if (EnablePayloadTraceLogging)
                {
                    _logger?.LogTrace("Received legacy metric payload for {Meter}/{Instrument}", legacyMetric.MeterName, legacyMetric.InstrumentName);
                }
                MetricReceived?.Invoke(legacyMetric);
                return true;
            }
        }
        catch (JsonException legacyEx)
        {
            _logger?.LogDebug(legacyEx, "Legacy metric payload deserialization failed");
        }

        try
        {
            var legacyActivity = JsonSerializer.Deserialize<ActivitySample>(buffer.Span, s_jsonOptions);
            if (legacyActivity is not null && !string.IsNullOrEmpty(legacyActivity.Name))
            {
                ActivityReceived?.Invoke(legacyActivity);
                return true;
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        if (_receiveTask is not null)
        {
            try
            {
                await _receiveTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Ignoring exception while awaiting UDP listener shutdown");
            }
        }

        if (_processingTasks is not null)
        {
            try
            {
                await Task.WhenAll(_processingTasks).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Ignoring exception while awaiting UDP processors shutdown");
            }
        }

        _udpClient.Dispose();
        _cts.Dispose();
        _logger?.LogInformation("UDP listener disposed");
    }
}
