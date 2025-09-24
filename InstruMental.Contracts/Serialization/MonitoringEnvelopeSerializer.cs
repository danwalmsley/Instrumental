using System;
using InstruMental.Contracts.Monitoring;

namespace InstruMental.Contracts.Serialization;

public static class MonitoringEnvelopeSerializer
{
    public static byte[] Serialize(MonitoringEnvelope envelope, EnvelopeEncoding encoding)
        => encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.Serialize(envelope),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.Serialize(envelope),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };

    public static bool TryDeserialize(Memory<byte> payload, EnvelopeEncoding encoding, out MonitoringEnvelope? envelope)
    {
        return encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.TryDeserialize(payload.Span, out envelope),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.TryDeserialize(payload, out envelope),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };
    }

    public static bool TryDeserialize(Memory<byte> payload, out MonitoringEnvelope? envelope)
    {
        if (JsonEnvelopeSerializer.TryDeserialize(payload.Span, out envelope))
        {
            return true;
        }

        return BinaryEnvelopeSerializer.TryDeserialize(payload, out envelope);
    }

    // Prefer ReadOnlyMemory<byte> overload so callers that already have Memory avoid a copy
    public static bool TryDeserializeBatch(ReadOnlyMemory<byte> payload, EnvelopeEncoding encoding, out List<MonitoringEnvelope> envelopes)
    {
        return encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.TryDeserializeBatch(payload.Span, out envelopes),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.TryDeserializeBatch(payload, out envelopes),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };
    }

    // ReadOnlySpan variant: will convert to Memory when needed (may allocate for binary)
    public static bool TryDeserializeBatch(ReadOnlySpan<byte> payload, EnvelopeEncoding encoding, out List<MonitoringEnvelope> envelopes)
    {
        return encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.TryDeserializeBatch(payload, out envelopes),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.TryDeserializeBatch(payload.ToArray(), out envelopes),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };
    }

    // Try deserialize without known encoding: prefer JSON first, then binary.
    public static bool TryDeserializeBatch(ReadOnlyMemory<byte> payload, out List<MonitoringEnvelope> envelopes)
    {
        if (JsonEnvelopeSerializer.TryDeserializeBatch(payload.Span, out envelopes))
        {
            return true;
        }

        return BinaryEnvelopeSerializer.TryDeserializeBatch(payload, out envelopes);
    }

    public static bool TryDeserializeBatch(ReadOnlySpan<byte> payload, out List<MonitoringEnvelope> envelopes)
    {
        if (JsonEnvelopeSerializer.TryDeserializeBatch(payload, out envelopes))
        {
            return true;
        }

        return BinaryEnvelopeSerializer.TryDeserializeBatch(payload.ToArray(), out envelopes);
    }

    // Convenience byte[] overloads to avoid caller needing to construct Memory explicitly
    public static bool TryDeserializeBatch(byte[] payload, EnvelopeEncoding encoding, out List<MonitoringEnvelope> envelopes)
        => TryDeserializeBatch(payload.AsMemory(), encoding, out envelopes);

    public static bool TryDeserializeBatch(byte[] payload, out List<MonitoringEnvelope> envelopes)
        => TryDeserializeBatch(payload.AsMemory(), out envelopes);
}