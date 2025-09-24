using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using InstruMental.Contracts.Monitoring;
using InstruMental.Contracts.Serialization;

namespace InstruMental.Services;

public sealed class InstruMentalActivityListener : IAsyncDisposable, IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public InstruMentalActivityListener(InstruMentalActivityListenerOptions? options = null)
    {
        options ??= new InstruMentalActivityListenerOptions();
        _udpClient = new UdpClient(AddressFamily.InterNetwork);
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsWindows())
        {
            _udpClient.Client.ExclusiveAddressUse = false;
        }
        _udpClient.Client.Bind(new IPEndPoint(options.ListenAddress, options.Port));
        PreferredEncoding = options.PreferredEncoding;
    }

    public EnvelopeEncoding? PreferredEncoding { get; }

    public event Action<ActivitySample>? ActivityReceived;
    public event Action<MetricSample>? MetricReceived;

    public void Start()
    {
        if (_receiveLoop is not null)
        {
            return;
        }
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udpClient.ReceiveAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (TryHandleBatch(result.Buffer))
                {
                    continue;
                }

                if (TryReadSingle(result.Buffer, out var env) && env is not null)
                {
                    if (env.Activity is { } act)
                    {
                        ActivityReceived?.Invoke(act);
                    }
                    continue;
                }

                // Legacy fallback: try direct ActivitySample JSON
                try
                {
                    var legacy = JsonSerializer.Deserialize<ActivitySample>(result.Buffer, _jsonOptions);
                    if (legacy is not null && !string.IsNullOrEmpty(legacy.Name))
                    {
                        ActivityReceived?.Invoke(legacy);
                    }
                }
                catch
                {
                    // ignore malformed
                }
            }
        }
        finally
        {
            _udpClient.Close();
        }
    }

    private bool TryHandleBatch(byte[] buffer)
    {
        if ((PreferredEncoding.HasValue && MonitoringEnvelopeSerializer.TryDeserializeBatch(buffer, PreferredEncoding.Value, out var envs)) ||
            MonitoringEnvelopeSerializer.TryDeserializeBatch(buffer, out envs))
        {
            foreach (var env in envs)
            {
                if (env.Activity is { } act)
                {
                    ActivityReceived?.Invoke(act);
                }
                if (env.Metric is { } met)
                {
                    MetricReceived?.Invoke(met);
                }
            }
            return true;
        }
        return false;
    }

    private bool TryReadSingle(ReadOnlySpan<byte> buffer, out MonitoringEnvelope? env)
    {
        env = null;
        if (PreferredEncoding.HasValue && MonitoringEnvelopeSerializer.TryDeserialize(buffer, PreferredEncoding.Value, out env))
        {
            return true;
        }
        if (MonitoringEnvelopeSerializer.TryDeserialize(buffer, out env))
        {
            return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
            _receiveLoop = null;
        }
        _udpClient.Dispose();
        _cts.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}