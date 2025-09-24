using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace InstruMental.Diagnostics.Monitoring;

public static class InstruMentalMonitoringExtensions
{
    public static IDisposable AttachInstruMentalMonitoring(this Application application)
    {
        return AttachInstruMentalMonitoring(application, new InstruMentalMonitoringOptions());
    }

    public static IDisposable AttachInstruMentalMonitoring(this Application application, InstruMentalMonitoringOptions options)
    {
        if (application is null)
        {
            throw new ArgumentNullException(nameof(application));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);

        var publisher = new AvaloniaMetricsPublisher(options);
        var handle = new InstruMentalMonitoringHandle(publisher);

        if (application.ApplicationLifetime is IControlledApplicationLifetime lifetime)
        {
            handle.RegisterLifetime(lifetime);
        }

        return handle;
    }

    private sealed class InstruMentalMonitoringHandle : IDisposable
    {
        private readonly AvaloniaMetricsPublisher _publisher;
        private IControlledApplicationLifetime? _lifetime;
        private EventHandler<ControlledApplicationLifetimeExitEventArgs>? _exitHandler;
        private int _disposed;

        public InstruMentalMonitoringHandle(AvaloniaMetricsPublisher publisher)
        {
            _publisher = publisher;
        }

        public void RegisterLifetime(IControlledApplicationLifetime lifetime)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _exitHandler = (_, _) => Dispose();
            _lifetime = lifetime;
            lifetime.Exit += _exitHandler;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_lifetime is not null && _exitHandler is not null)
            {
                _lifetime.Exit -= _exitHandler;
            }

            _publisher.Dispose();
        }
    }
}
