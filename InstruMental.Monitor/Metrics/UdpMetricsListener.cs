using System;
using System.Net;
using System.Threading.Tasks;
using InstruMental.Contracts.Monitoring;
using InstruMental.Contracts.Serialization;
using InstruMental.Monitor.Infrastructure;

namespace InstruMental.Monitor.Metrics;

internal sealed class UdpMetricsListener : IAsyncDisposable
{
    private InstruMental.Diagnostics.Services.UdpMetricsListener? _shared;

    public UdpMetricsListener(int port, EnvelopeEncoding preferredEncoding = EnvelopeEncoding.Json)
    {
        // Create the shared diagnostics listener and forward events
        var logger = Log.For<UdpMetricsListener>();
        _shared = new InstruMental.Diagnostics.Services.UdpMetricsListener(port, preferredEncoding, IPAddress.Any, (Microsoft.Extensions.Logging.ILogger)logger);
        _shared.MetricReceived += (s) => MetricReceived?.Invoke(s);
        _shared.ActivityReceived += (a) => ActivityReceived?.Invoke(a);
    }

    public event Action<MetricSample>? MetricReceived;

    public event Action<ActivitySample>? ActivityReceived;

    public void Start()
    {
        _shared?.Start();
    }

    public async ValueTask DisposeAsync()
    {
        if (_shared is not null)
        {
            await _shared.DisposeAsync().ConfigureAwait(false);
            _shared = null;
        }
    }
}
