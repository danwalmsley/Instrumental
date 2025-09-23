using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Diagnostics.EventBroadcast.Messaging;

namespace Diagnostics.EventBroadcast;

public static class TimelineEventSerializer
{
    // Binary format version for future compatibility
    private const byte FormatVersion = 1;
    
    public static byte[] SerializeBatch(IList<TimelineEventMessage> messages)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        
        // Write format version and message count
        writer.Write(FormatVersion);
        writer.Write(messages.Count);
        
        foreach (var message in messages)
        {
            SerializeMessage(writer, message);
        }
        
        return stream.ToArray();
    }
    
    public static List<TimelineEventMessage> DeserializeBatch(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        
        var version = reader.ReadByte();
        if (version != FormatVersion)
        {
            throw new InvalidOperationException($"Unsupported format version: {version}");
        }
        
        var messageCount = reader.ReadInt32();
        var messages = new List<TimelineEventMessage>(messageCount);
        
        for (int i = 0; i < messageCount; i++)
        {
            messages.Add(DeserializeMessage(reader));
        }
        
        return messages;
    }
    
    private static void SerializeMessage(BinaryWriter writer, TimelineEventMessage message)
    {
        // Message type (1 byte)
        writer.Write((byte)message.MessageType);
        
        // EventId (16 bytes)
        writer.Write(message.EventId.ToByteArray());
        
        // Timestamp (8 bytes - ticks) + (4 bytes - offset minutes)
        writer.Write(message.Timestamp.Ticks);
        writer.Write((int)message.Timestamp.Offset.TotalMinutes);
        
        // Optional fields using flags byte
        byte flags = 0;
        if (message.Track != null) flags |= 0x01;
        if (message.Label != null) flags |= 0x02;
        if (message.ColorHex != null) flags |= 0x04;
        if (message.ParentEventId != null) flags |= 0x08;
        if (message.EndTimestamp != null) flags |= 0x10;  // Add EndTimestamp flag
        
        writer.Write(flags);
        
        // Write optional string fields
        if (message.Track != null)
        {
            WriteString(writer, message.Track);
        }
        
        if (message.Label != null)
        {
            WriteString(writer, message.Label);
        }
        
        if (message.ColorHex != null)
        {
            WriteString(writer, message.ColorHex);
        }
        
        // Write optional parent event ID
        if (message.ParentEventId != null)
        {
            writer.Write(message.ParentEventId.Value.ToByteArray());
        }
        
        // Write optional end timestamp
        if (message.EndTimestamp != null)
        {
            writer.Write(message.EndTimestamp.Value.Ticks);
            writer.Write((int)message.EndTimestamp.Value.Offset.TotalMinutes);
        }
    }
    
    private static TimelineEventMessage DeserializeMessage(BinaryReader reader)
    {
        // Read message type
        var messageType = (TimelineEventMessageType)reader.ReadByte();
        
        // Read EventId
        var eventIdBytes = reader.ReadBytes(16);
        var eventId = new Guid(eventIdBytes);
        
        // Read timestamp
        var ticks = reader.ReadInt64();
        var offsetMinutes = reader.ReadInt32();
        var timestamp = new DateTimeOffset(ticks, TimeSpan.FromMinutes(offsetMinutes));
        
        // Read flags
        var flags = reader.ReadByte();
        
        // Read optional fields
        string? track = null;
        string? label = null;
        string? colorHex = null;
        Guid? parentEventId = null;
        DateTimeOffset? endTimestamp = null;
        
        if ((flags & 0x01) != 0)
        {
            track = ReadString(reader);
        }
        
        if ((flags & 0x02) != 0)
        {
            label = ReadString(reader);
        }
        
        if ((flags & 0x04) != 0)
        {
            colorHex = ReadString(reader);
        }
        
        if ((flags & 0x08) != 0)
        {
            var parentIdBytes = reader.ReadBytes(16);
            parentEventId = new Guid(parentIdBytes);
        }
        
        if ((flags & 0x10) != 0)
        {
            var endTicks = reader.ReadInt64();
            var endOffsetMinutes = reader.ReadInt32();
            endTimestamp = new DateTimeOffset(endTicks, TimeSpan.FromMinutes(endOffsetMinutes));
        }
        
        return new TimelineEventMessage(messageType, eventId, timestamp, track, label, colorHex, parentEventId, endTimestamp);
    }
    
    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }
    
    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        var bytes = reader.ReadBytes(length);
        return Encoding.UTF8.GetString(bytes);
    }
}
