using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Diagnostics.EventBroadcast;
using Diagnostics.EventBroadcast.Messaging;
using Diagnostics.ViewModels;

namespace Diagnostics.Services;

public sealed class TimelineEventListener : IAsyncDisposable, IDisposable
{
    private readonly TimelineViewModel _timeline;
    private readonly TimelineEventListenerOptions _options;
    private readonly JsonSerializerOptions _serializerOptions;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;
    private UdpClient? _udpClient;
    private bool _disposed;

    public TimelineEventListener(TimelineViewModel timeline, TimelineEventListenerOptions? options = null)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        _options = options ?? new TimelineEventListenerOptions();
        _serializerOptions = TimelineEventSerializer.Options;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_receiveLoop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _udpClient = CreateUdpClient();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private UdpClient CreateUdpClient()
    {
        var udpClient = new UdpClient(AddressFamily.InterNetwork);
        udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

        if (OperatingSystem.IsWindows())
        {
            udpClient.Client.ExclusiveAddressUse = false;
        }

        udpClient.EnableBroadcast = true;
        udpClient.Client.Bind(new IPEndPoint(_options.ListenAddress, _options.Port));
        return udpClient;
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_udpClient is null)
        {
            return;
        }

        var udpClient = _udpClient;
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udpClient.ReceiveAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            if (result.Buffer.Length == 0)
            {
                continue;
            }

            TimelineEventMessage? singleMessage = null;
            TimelineEventMessage[]? batch = null;
            try
            {
                var buffer = result.Buffer;
                var index = 0;
                while (index < buffer.Length && char.IsWhiteSpace((char)buffer[index]))
                {
                    index++;
                }

                var isArray = index < buffer.Length && buffer[index] == '[';
                if (isArray)
                {
                    batch = JsonSerializer.Deserialize<TimelineEventMessage[]>(buffer, _serializerOptions);
                }
                else
                {
                    singleMessage = JsonSerializer.Deserialize<TimelineEventMessage>(buffer, _serializerOptions);
                }
            }
            catch (JsonException)
            {
                // Ignore invalid payloads
            }

            if (batch is { Length: > 0 })
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var message in batch)
                    {
                        ApplyMessage(message);
                    }
                }, DispatcherPriority.Background);

                continue;
            }

            if (singleMessage is null)
            {
                continue;
            }

            await Dispatcher.UIThread.InvokeAsync(() => ApplyMessage(singleMessage), DispatcherPriority.Background);
        }
    }

    private void ApplyMessage(TimelineEventMessage message)
    {
        switch (message.MessageType)
        {
            case TimelineEventMessageType.Start:
                if (message.Track is null || message.Label is null || message.ColorHex is null)
                {
                    return;
                }

                if (!TryParseColor(message.ColorHex, out var color))
                {
                    return;
                }

                _timeline.ApplyEventStart(message.EventId, message.Track, message.Timestamp, message.Label, color, message.ParentEventId);
                break;
            case TimelineEventMessageType.Stop:
                _timeline.ApplyEventStop(message.EventId, message.Timestamp);
                break;
        }
    }

    private static bool TryParseColor(string value, out Avalonia.Media.Color color)
    {
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        return Avalonia.Media.Color.TryParse(value, out color);
    }

    public async Task StopAsync()
    {
        if (_receiveLoop is null)
        {
            return;
        }

        var cts = _cts;
        if (cts is not null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
        }

        var udpClient = _udpClient;
        _udpClient = null;
        udpClient?.Close();

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
        finally
        {
            _receiveLoop = null;
        }

        udpClient?.Dispose();
        cts?.Dispose();
        _cts = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TimelineEventListener));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
