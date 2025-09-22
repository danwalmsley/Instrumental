using System;
using System.Net;

namespace Diagnostics.EventBroadcast;

public sealed class TimelineEventBroadcasterOptions
{
    public const int DefaultPort = 41234;

    public IPAddress BroadcastAddress { get; init; } = IPAddress.Broadcast;

    public int Port { get; init; } = DefaultPort;

    public TimeSpan? SocketSendTimeout { get; init; }
}
