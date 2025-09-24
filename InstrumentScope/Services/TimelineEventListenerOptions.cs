using System.Net;
using InstruMental.EventBroadcast;

namespace InstruMental.Services;

public sealed class TimelineEventListenerOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    public int Port { get; init; } = TimelineEventBroadcasterOptions.DefaultPort;
}
