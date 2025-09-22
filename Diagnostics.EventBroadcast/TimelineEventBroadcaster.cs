using System;
using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<TimelineEventMessage> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private readonly TimeSpan _flushInterval;
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
        _flushInterval = options.FlushInterval <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(100)
            : options.FlushInterval;

        _processingTask = Task.Run(() => ProcessQueueAsync(_cts.Token));
    }

    public void EnqueueStart(TimelineEventStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        EnqueueMessage(TimelineEventMessage.CreateStart(start));
    }

    public void EnqueueStop(TimelineEventStop stop)
    {
        ArgumentNullException.ThrowIfNull(stop);
        EnqueueMessage(TimelineEventMessage.CreateStop(stop));
    }

    private void EnqueueMessage(TimelineEventMessage message)
    {
        ThrowIfDisposed();
        _queue.Enqueue(message);
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_flushInterval, cancellationToken).ConfigureAwait(false);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // expected during disposal
        }
        finally
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_queue.IsEmpty)
        {
            return;
        }

        var client = _udpClient;
        if (client is null)
        {
            return;
        }

        while (_queue.TryDequeue(out var message))
        {
            var buffer = JsonSerializer.SerializeToUtf8Bytes(message, TimelineEventSerializer.Options);
            await client.SendAsync(buffer, buffer.Length, _broadcastEndpoint).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TimelineEventBroadcaster));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        try
        {
            await _processingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown
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
