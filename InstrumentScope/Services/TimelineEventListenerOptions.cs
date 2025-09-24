using System.Net;
using InstrumentScope.EventBroadcast;

namespace InstrumentScope.Services;

public sealed class TimelineEventListenerOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    public int Port { get; init; } = TimelineEventBroadcasterOptions.DefaultPort;
}
