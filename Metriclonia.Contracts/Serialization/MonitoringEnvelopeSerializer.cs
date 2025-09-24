using System;
using System.Collections.Generic;
using Metriclonia.Contracts.Monitoring;

namespace Metriclonia.Contracts.Serialization;

public static class MonitoringEnvelopeSerializer
{
    public static byte[] Serialize(MonitoringEnvelope envelope, EnvelopeEncoding encoding)
        => encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.Serialize(envelope),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.Serialize(envelope),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, EnvelopeEncoding encoding, out MonitoringEnvelope? envelope)
    {
        return encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.TryDeserialize(payload, out envelope),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.TryDeserialize(payload, out envelope),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };
    }

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out MonitoringEnvelope? envelope)
    {
        if (JsonEnvelopeSerializer.TryDeserialize(payload, out envelope))
        {
            return true;
        }

        return BinaryEnvelopeSerializer.TryDeserialize(payload, out envelope);
    }

    public static byte[] SerializeBatch(IReadOnlyList<MonitoringEnvelope> envelopes, EnvelopeEncoding encoding)
        => encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.SerializeBatch(envelopes),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.SerializeBatch(envelopes),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };

    public static bool TryDeserializeBatch(ReadOnlySpan<byte> payload, EnvelopeEncoding encoding, out List<MonitoringEnvelope> envelopes)
    {
        return encoding switch
        {
            EnvelopeEncoding.Json => JsonEnvelopeSerializer.TryDeserializeBatch(payload, out envelopes),
            EnvelopeEncoding.Binary => BinaryEnvelopeSerializer.TryDeserializeBatch(payload, out envelopes),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding), encoding, null)
        };
    }

    public static bool TryDeserializeBatch(ReadOnlySpan<byte> payload, out List<MonitoringEnvelope> envelopes)
    {
        if (JsonEnvelopeSerializer.TryDeserializeBatch(payload, out envelopes))
        {
            return true;
        }

        return BinaryEnvelopeSerializer.TryDeserializeBatch(payload, out envelopes);
    }
}
