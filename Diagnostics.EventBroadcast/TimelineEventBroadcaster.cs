using System;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Diagnostics.EventBroadcast.Messaging;

namespace Diagnostics.EventBroadcast;

public sealed class TimelineEventBroadcaster : IAsyncDisposable, IDisposable
{
    private readonly UdpClient _udpClient;
    private readonly IPEndPoint _broadcastEndpoint;
    private bool _disposed;

    public TimelineEventBroadcaster(TimelineEventBroadcasterOptions? options = null)
    {
        options ??= new TimelineEventBroadcasterOptions();

        if (options.Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Port), "Port must be between 1 and 65535.");
        }

        _udpClient = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true
        };

        if (options.SocketSendTimeout is { } timeout && timeout > TimeSpan.Zero)
        {
            _udpClient.Client.SendTimeout = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
        }

        _broadcastEndpoint = new IPEndPoint(options.BroadcastAddress, options.Port);
    }

    public Task SendStartAsync(TimelineEventStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        return SendAsync(TimelineEventMessage.CreateStart(start), cancellationToken);
    }

    public Task SendStopAsync(TimelineEventStop stop, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stop);
        return SendAsync(TimelineEventMessage.CreateStop(stop), cancellationToken);
    }

    private async Task SendAsync(TimelineEventMessage message, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var buffer = JsonSerializer.SerializeToUtf8Bytes(message, TimelineEventSerializer.Options);
        await _udpClient.SendAsync(buffer, buffer.Length, _broadcastEndpoint).WaitAsync(cancellationToken);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TimelineEventBroadcaster));
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _udpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _ = DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
