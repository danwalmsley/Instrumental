using System;
using InstruMental.Contracts.Serialization;

namespace InstruMental.Diagnostics.Monitoring;

public sealed class InstruMentalMonitoringOptions
{
    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 5005;

    public TimeSpan ObservableInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public EnvelopeEncoding Encoding { get; init; } = EnvelopeEncoding.Json;

    // New: batching controls
    public TimeSpan BatchFlushInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public int MaxBatchSize { get; init; } = 100;
}
