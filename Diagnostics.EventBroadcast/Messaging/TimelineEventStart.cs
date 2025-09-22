using System;

namespace Diagnostics.EventBroadcast.Messaging;

public sealed record TimelineEventStart
{
    public TimelineEventStart(Guid eventId, string track, string label, string colorHex, DateTimeOffset timestamp, Guid? parentEventId = null)
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

        EventId = eventId == Guid.Empty ? Guid.NewGuid() : eventId;
        Track = track;
        Label = label;
        ColorHex = NormalizeHex(colorHex);
        Timestamp = timestamp;
        ParentEventId = parentEventId;
    }

    public Guid EventId { get; }

    public string Track { get; }

    public string Label { get; }

    public string ColorHex { get; }

    public DateTimeOffset Timestamp { get; }

    public Guid? ParentEventId { get; }

    private static string NormalizeHex(string value)
    {
        value = value.Trim();
        if (!value.StartsWith('#'))
        {
            value = "#" + value;
        }

        if (value.Length == 7)
        {
            return value.ToUpperInvariant();
        }

        if (value.Length == 9)
        {
            return value.ToUpperInvariant();
        }

        throw new ArgumentException("Color must be in #RRGGBB or #AARRGGBB format.", nameof(value));
    }
}
