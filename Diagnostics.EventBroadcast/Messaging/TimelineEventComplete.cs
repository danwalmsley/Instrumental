using System;

namespace Diagnostics.EventBroadcast.Messaging;

public sealed record TimelineEventComplete
{
    public TimelineEventComplete(Guid eventId, string track, string label, string colorHex, DateTimeOffset startTimestamp, DateTimeOffset endTimestamp, Guid? parentEventId = null)
    {
        if (string.IsNullOrWhiteSpace(track))
        {
            throw new ArgumentException("Track is required.", nameof(track));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Label is required.", nameof(label));
        }

        if (string.IsNullOrWhiteSpace(colorHex))
        {
            throw new ArgumentException("Color is required.", nameof(colorHex));
        }

        if (endTimestamp < startTimestamp)
        {
            throw new ArgumentException("End timestamp must be after start timestamp.", nameof(endTimestamp));
        }

        EventId = eventId == Guid.Empty ? Guid.NewGuid() : eventId;
        Track = track;
        Label = label;
        ColorHex = NormalizeHex(colorHex);
        StartTimestamp = startTimestamp;
        EndTimestamp = endTimestamp;
        ParentEventId = parentEventId;
    }

    public Guid EventId { get; }

    public string Track { get; }

    public string Label { get; }

    public string ColorHex { get; }

    public DateTimeOffset StartTimestamp { get; }

    public DateTimeOffset EndTimestamp { get; }

    public Guid? ParentEventId { get; }

    public TimeSpan Duration => EndTimestamp - StartTimestamp;

    private static string NormalizeHex(string value)
    {
        value = value.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        return value;
    }
}
