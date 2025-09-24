using System;

namespace InstrumentScope.EventBroadcast.Messaging;

public sealed record TimelineEventStop
{
    public TimelineEventStop(Guid eventId, DateTimeOffset timestamp)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("EventId must be a non-empty GUID.", nameof(eventId));
        }

        EventId = eventId;
        Timestamp = timestamp;
    }

    public Guid EventId { get; }

    public DateTimeOffset Timestamp { get; }
}
