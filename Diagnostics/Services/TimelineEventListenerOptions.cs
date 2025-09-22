using System.Net;
using Diagnostics.EventBroadcast;

namespace Diagnostics.Services;

public sealed class TimelineEventListenerOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;

    public int Port { get; init; } = TimelineEventBroadcasterOptions.DefaultPort;
}
