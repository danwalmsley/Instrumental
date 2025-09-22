using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering;
using Diagnostics.EventBroadcast;
using Diagnostics.EventBroadcast.Messaging;
using Diagnostics.Services;
using ReactiveUI;
using System.Reactive;
using System.Reactive.Linq;

namespace Diagnostics.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable, IDisposable
{
    private readonly TimelineEventListener? _listener;
    private readonly TimelineEventBroadcaster? _broadcaster;
    private bool _disposed;
    private CancellationTokenSource? _demoCts;
    private Task? _demoTask;
    private readonly List<IDisposable> _commandsToDispose = new();

    public MainWindowViewModel()
    {
        RunDemoCommand = RegisterCommand(ReactiveCommand.CreateFromTask(RunDemoAsync, outputScheduler: RxApp.MainThreadScheduler));
        StartCaptureCommand = CreateCommand(Timeline.StartCapture, () => !Timeline.IsCapturing, nameof(TimelineViewModel.IsCapturing));
        StopCaptureCommand = CreateCommand(Timeline.StopCapture, () => Timeline.IsCapturing, nameof(TimelineViewModel.IsCapturing));
        ZoomInCommand = CreateCommand(() => Timeline.AdjustZoom(0.5));
        ZoomOutCommand = CreateCommand(() => Timeline.AdjustZoom(2.0));
        ResetZoomCommand = CreateCommand(Timeline.ResetZoom);
        GoLiveCommand = CreateCommand(Timeline.GoLive, () => !Timeline.IsLive, nameof(TimelineViewModel.IsLive));
        ClearCommand = CreateCommand(Timeline.ClearTimeline);

        if (Design.IsDesignMode)
        {
            SeedDesignTimeData();
        }
        else
        {
            var listenerOptions = new TimelineEventListenerOptions
            {
                ListenAddress = IPAddress.Loopback
            };
            _listener = new TimelineEventListener(Timeline, listenerOptions);
            _listener.Start();
            var broadcasterOptions = new TimelineEventBroadcasterOptions
            {
                BroadcastAddress = IPAddress.Loopback
            };
            _broadcaster = new TimelineEventBroadcaster(broadcasterOptions);
        }
    }

    public TimelineViewModel Timeline { get; } = new();

    public ICommand RunDemoCommand { get; }
    public ICommand StartCaptureCommand { get; }
    public ICommand StopCaptureCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ResetZoomCommand { get; }
    public ICommand GoLiveCommand { get; }
    public ICommand ClearCommand { get; }

    private void SeedDesignTimeData()
    {
        var now = DateTimeOffset.UtcNow;
        var parentId = Guid.NewGuid();
        Timeline.ApplyEventStart(parentId, "Main", now - TimeSpan.FromSeconds(25), "Parent Event", Colors.CadetBlue);
        Timeline.ApplyEventStop(parentId, now - TimeSpan.FromSeconds(5));

        var childId = Guid.NewGuid();
        Timeline.ApplyEventStart(childId, "Main", now - TimeSpan.FromSeconds(20), "Child A", Colors.OrangeRed, parentId);
        Timeline.ApplyEventStop(childId, now - TimeSpan.FromSeconds(10));

        var childId2 = Guid.NewGuid();
        Timeline.ApplyEventStart(childId2, "Main", now - TimeSpan.FromSeconds(12), "Child B", Colors.Gold, parentId);
        Timeline.ApplyEventStop(childId2, now - TimeSpan.FromSeconds(2));

        var activeId = Guid.NewGuid();
        Timeline.ApplyEventStart(activeId, "Secondary", now - TimeSpan.FromSeconds(8), "Active Event", Colors.MediumSeaGreen);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelDemo();
        if (_demoTask is { } runningDemo)
        {
            try
            {
                await runningDemo.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelling the demo.
            }
        }

        foreach (var command in _commandsToDispose)
        {
            command.Dispose();
        }
        _commandsToDispose.Clear();

        Timeline.Dispose();

        if (_listener is not null)
        {
            await _listener.DisposeAsync();
        }

        if (_broadcaster is not null)
        {
            await _broadcaster.DisposeAsync();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public Guid BroadcastStart(string track, string label, Color color, Guid? eventId = null, Guid? parentId = null, DateTimeOffset? timestamp = null, bool applyLocally = true)
    {
        if (string.IsNullOrWhiteSpace(track))
        {
            throw new ArgumentException("Track is required.", nameof(track));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Label is required.", nameof(label));
        }

        var id = eventId ?? Guid.NewGuid();
        var timestampValue = timestamp ?? DateTimeOffset.UtcNow;

        if (applyLocally || _broadcaster is null)
        {
            Timeline.ApplyEventStart(id, track, timestampValue, label, color, parentId);
        }

        if (_broadcaster is not null)
        {
            var colorHex = ToHex(color);
            var start = new TimelineEventStart(id, track, label, colorHex, timestampValue, parentId);
            _broadcaster.EnqueueStart(start);
        }

        return id;
    }

    public void BroadcastStop(Guid eventId, DateTimeOffset? timestamp = null, bool applyLocally = true)
    {
        var timestampValue = timestamp ?? DateTimeOffset.UtcNow;

        if (applyLocally || _broadcaster is null)
        {
            Timeline.ApplyEventStop(eventId, timestampValue);
        }

        if (_broadcaster is not null)
        {
            var stop = new TimelineEventStop(eventId, timestampValue);
            _broadcaster.EnqueueStop(stop);
        }
    }

    private async Task RunDemoAsync(CancellationToken cancellationToken)
    {
        CancelDemo();

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _demoCts = linkedCts;
        var demoTask = RunDemoInternalAsync(linkedCts.Token);
        _demoTask = demoTask;

        try
        {
            await demoTask.ConfigureAwait(false);
        }
        finally
        {
            linkedCts.Dispose();

            if (ReferenceEquals(_demoTask, demoTask))
            {
                _demoTask = null;
            }

            if (ReferenceEquals(_demoCts, linkedCts))
            {
                _demoCts = null;
            }
        }
    }

    private long _renderTickCounter;

    private async Task RunDemoInternalAsync(CancellationToken cancellationToken)
    {
        var renderTimer = GetRequiredService<IRenderTimer>();
        if (renderTimer is null)
        {
            throw new InvalidOperationException("IRenderTimer service is not available.");
        }

        _renderTickCounter = 0;
        var renderTrack = "Render Loop";
        Guid? lastRenderEventId = null;

        void OnRender(TimeSpan _)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;

            if (lastRenderEventId is { } previousId)
            {
                BroadcastStop(previousId, now, applyLocally: false);
            }

            var tick = Interlocked.Increment(ref _renderTickCounter);
            lastRenderEventId = BroadcastStart(renderTrack, $"Render Tick {tick}", Colors.LightSkyBlue, Guid.NewGuid(), null, now, applyLocally: false);
        }

        renderTimer.Tick += OnRender;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // expected when cancelled
        }
        finally
        {
            renderTimer.Tick -= OnRender;

            if (lastRenderEventId is { } finalId)
            {
                BroadcastStop(finalId, DateTimeOffset.UtcNow, applyLocally: false);
            }
        }
    }


    private void CancelDemo()
    {
        _demoCts?.Cancel();
    }

    private static string ToHex(Color color) =>
        color.A < byte.MaxValue
            ? $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private ICommand CreateCommand(Action execute, Func<bool>? canExecuteEvaluator = null, params string[] observedTimelineProperties)
    {
        if (canExecuteEvaluator is null)
        {
            return RegisterCommand(ReactiveCommand.Create(execute));
        }

        var observedProperties = observedTimelineProperties is { Length: > 0 }
            ? observedTimelineProperties
            : Array.Empty<string>();

        var propertyChanges = Observable
            .FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                handler => Timeline.PropertyChanged += handler,
                handler => Timeline.PropertyChanged -= handler)
            .Where(evt => string.IsNullOrEmpty(evt.EventArgs.PropertyName) || observedProperties.Length == 0 || Array.IndexOf(observedProperties, evt.EventArgs.PropertyName) >= 0)
            .Select(_ => canExecuteEvaluator());

        var canExecute = Observable.Return(canExecuteEvaluator()).Concat(propertyChanges);

        return RegisterCommand(ReactiveCommand.Create(execute, canExecute));
    }

    private ICommand RegisterCommand(ICommand command)
    {
        if (command is IDisposable disposable)
        {
            _commandsToDispose.Add(disposable);
        }

        return command;
    }

    private static T GetRequiredService<T>() where T : class
    {
        return (T)(AvaloniaLocator.Current.GetService(typeof(T)) ?? throw new InvalidOperationException($"Service of type {typeof(T)} not found."));
    }
}
