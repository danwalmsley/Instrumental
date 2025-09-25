using System;
using System.Net;
using System.Threading.Tasks;
using InstruMental.Contracts.Monitoring;
using InstruMental.Contracts.Serialization;
using InstruMental.Diagnostics.Services;

namespace InstruMental.Services;

public sealed class InstruMentalActivityListener : IAsyncDisposable, IDisposable
{
    private readonly UdpMetricsListener _inner;

    public InstruMentalActivityListener(InstruMentalActivityListenerOptions? options = null)
    {
        options ??= new InstruMentalActivityListenerOptions();
        PreferredEncoding = options.PreferredEncoding;
        _inner = new UdpMetricsListener(options.Port, options.PreferredEncoding, options.ListenAddress, null);
        _inner.MetricReceived += (m) => MetricReceived?.Invoke(m);
        _inner.ActivityReceived += (a) => ActivityReceived?.Invoke(a);
    }

    public EnvelopeEncoding? PreferredEncoding { get; }

    public event Action<ActivitySample>? ActivityReceived;
    public event Action<MetricSample>? MetricReceived;

    public void Start() => _inner.Start();

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}