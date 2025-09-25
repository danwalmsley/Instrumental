using System;
using InstruMental.Contracts.Serialization;

namespace InstruMental.Diagnostics.Monitoring;

public sealed class InstruMentalMonitoringOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5005;

    public TimeSpan ObservableInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public EnvelopeEncoding Encoding { get; init; } = EnvelopeEncoding.Binary;

    // New: batching controls
    public TimeSpan BatchFlushInterval { get; init; } = TimeSpan.FromMilliseconds(20);

    public int MaxBatchSize { get; init; } = 100;

    // New: approximate maximum datagram payload size (in bytes) to avoid UDP fragmentation.
    // Default 1200 is conservative for typical MTUs; adjust for your network (e.g., 1400 or 1500-MTU minus IP/UDP headers).
    public int MaxDatagramSize { get; init; } = 1200;
}
