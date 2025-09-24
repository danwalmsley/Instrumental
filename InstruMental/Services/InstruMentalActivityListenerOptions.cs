using System.Net;
using InstruMental.Contracts.Serialization;

namespace InstruMental.Services;

public sealed class InstruMentalActivityListenerOptions
{
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;
    public int Port { get; init; } = 5005;
    public EnvelopeEncoding? PreferredEncoding { get; init; } = null; // Auto-detect by default
}
