using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Metriclonia.Contracts.Monitoring;

namespace Metriclonia.Contracts.Serialization;

public static class JsonEnvelopeSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static byte[] Serialize(MonitoringEnvelope envelope)
        => JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    public static bool TryDeserialize(ReadOnlySpan<byte> payload, out MonitoringEnvelope? envelope)
    {
        try
        {
            envelope = JsonSerializer.Deserialize<MonitoringEnvelope>(payload, Options);
            return envelope is not null;
        }
        catch (JsonException)
        {
            envelope = null;
            return false;
        }
    }

    public static byte[] SerializeBatch(IReadOnlyList<MonitoringEnvelope> envelopes)
        => JsonSerializer.SerializeToUtf8Bytes(envelopes, Options);

    public static bool TryDeserializeBatch(ReadOnlySpan<byte> payload, out List<MonitoringEnvelope> envelopes)
    {
        envelopes = new List<MonitoringEnvelope>();
        try
        {
            // First try array of envelopes
            var arr = JsonSerializer.Deserialize<List<MonitoringEnvelope>>(payload, Options);
            if (arr is { Count: > 0 })
            {
                envelopes = arr;
                return true;
            }
        }
        catch (JsonException)
        {
            // Fallback to single envelope
        }

        try
        {
            if (TryDeserialize(payload, out var single) && single is not null)
            {
                envelopes = new List<MonitoringEnvelope> { single };
                return true;
            }
        }
        catch (JsonException)
        {
        }

        envelopes = new List<MonitoringEnvelope>();
        return false;
    }
}
