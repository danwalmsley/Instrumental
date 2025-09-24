using System;
using System.Net;

namespace InstrumentScope.EventBroadcast;

public sealed class TimelineEventBroadcasterOptions
{
    public const int DefaultPort = 41234;

    public IPAddress BroadcastAddress { get; init; } = IPAddress.Loopback;

    public int Port { get; init; } = DefaultPort;

    public TimeSpan? SocketSendTimeout { get; init; }

    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(100);
}
