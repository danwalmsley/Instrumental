using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diagnostics.EventBroadcast;

public static class TimelineEventSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
