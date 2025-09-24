using System;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using InstruMental.Avalonia;
using InstruMental.EventBroadcast;

namespace InstruMental.SampleApp;

public sealed partial class App : Application
{
    private TimelineEventBroadcaster? _broadcaster;
    //private AvaloniaDiagnosticsInstrumentor? _instrumentor;
    private AvaloniaMetricsInstrumentor? _metricsInstrumentor; // added metrics instrumentor
    private CancellationTokenSource? _cts;
    private Task? _instrumentationTask;
    private Task? _metricsTask; // task for metrics listener

    private static readonly DispatcherPriority[] WorkPriorities = new[]
    {
        DispatcherPriority.Normal,
        //DispatcherPriority.Render,
        //DispatcherPriority.Input
    };

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            desktop.Exit += OnExit;
            StartDiagnostics();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void StartDiagnostics()
    {
        _broadcaster = new TimelineEventBroadcaster();
        //_instrumentor = new AvaloniaDiagnosticsInstrumentor();
        _metricsInstrumentor = new AvaloniaMetricsInstrumentor(_broadcaster, new[] { "Avalonia", "System.Runtime" });
        _cts = new CancellationTokenSource();
        //_instrumentationTask = _instrumentor.InstrumentAvalonia(_broadcaster, null, _cts.Token);
        _metricsTask = _metricsInstrumentor.RunAsync(_cts.Token);
        StartSyntheticDispatcherWork(_cts.Token);
    }

    private void StartSyntheticDispatcherWork(CancellationToken token)
    {
        foreach (var priority in WorkPriorities)
        {
            if (priority == DispatcherPriority.Send || priority == DispatcherPriority.SystemIdle)
                continue;
            ScheduleWork(priority, token);
        }
    }

    private void ScheduleWork(DispatcherPriority priority, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (token.IsCancellationRequested) return;
            var sw = Stopwatch.StartNew();
            // Busy-loop for ~5ms to attribute CPU time to this priority
            while (sw.ElapsedMilliseconds < 1)
            {
                // Optionally do trivial ops to avoid tight no-op optimization
                _ = sw.ElapsedTicks;
            }
            if (!token.IsCancellationRequested)
            {
                // Re-schedule at an idle priority so other queued higher priorities can run first.
                Dispatcher.UIThread.Post(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        ScheduleWork(priority, token);
                    }
                }, DispatcherPriority.ContextIdle);
            }
        }, priority);
    }

    private async void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        try
        {
            _cts?.Cancel();
            if (_instrumentationTask is not null)
            {
                try { await _instrumentationTask.ConfigureAwait(false); } catch { /* swallow */ }
            }
            if (_metricsTask is not null)
            {
                try { await _metricsTask.ConfigureAwait(false); } catch { /* swallow */ }
            }
        }
        finally
        {
            _cts?.Dispose();
            _metricsInstrumentor?.Dispose();
            _broadcaster?.Dispose();
        }
    }
}
