using System;
using System.Text.Json.Serialization;

namespace InstruMental.EventBroadcast.Messaging;

public sealed record TimelineEventMessage
{
    [JsonConstructor]
    public TimelineEventMessage(
        TimelineEventMessageType messageType,
        Guid eventId,
        DateTimeOffset timestamp,
        string? track,
        string? label,
        string? colorHex,
        Guid? parentEventId,
        DateTimeOffset? endTimestamp = null)
    {
        MessageType = messageType;
        EventId = eventId;
        Timestamp = timestamp;
        Track = track;
        Label = label;
        ColorHex = colorHex;
        ParentEventId = parentEventId;
        EndTimestamp = endTimestamp;
    }

    public TimelineEventMessageType MessageType { get; }

    public Guid EventId { get; }

    public DateTimeOffset Timestamp { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Track { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorHex { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentEventId { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? EndTimestamp { get; }

    public static TimelineEventMessage CreateStart(TimelineEventStart start)
    {
        if (start is null)
        {
            throw new ArgumentNullException(nameof(start));
        }

        return new TimelineEventMessage(
            TimelineEventMessageType.Start,
            start.EventId,
            start.Timestamp,
            start.Track,
            start.Label,
            start.ColorHex,
            start.ParentEventId);
    }

    public static TimelineEventMessage CreateStop(TimelineEventStop stop)
    {
        if (stop is null)
        {
            throw new ArgumentNullException(nameof(stop));
        }

        return new TimelineEventMessage(
            TimelineEventMessageType.Stop,
            stop.EventId,
            stop.Timestamp,
            null,
            null,
            null,
            null);
    }

    public static TimelineEventMessage CreateComplete(TimelineEventComplete complete)
    {
        if (complete is null)
        {
            throw new ArgumentNullException(nameof(complete));
        }

        return new TimelineEventMessage(
            TimelineEventMessageType.Complete,
            complete.EventId,
            complete.StartTimestamp,
            complete.Track,
            complete.Label,
            complete.ColorHex,
            complete.ParentEventId,
            complete.EndTimestamp);
    }
}
