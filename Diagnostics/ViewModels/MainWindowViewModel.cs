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

    public Task BroadcastStartAsync(string track, string label, Color color, Guid? eventId = null, Guid? parentId = null, DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default, bool applyLocally = true)
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

        if (_broadcaster is null)
        {
            return Task.CompletedTask;
        }

        var colorHex = ToHex(color);
        var start = new TimelineEventStart(id, track, label, colorHex, timestampValue, parentId);
        return _broadcaster.SendStartAsync(start, cancellationToken);
    }

    public Task BroadcastStopAsync(Guid eventId, DateTimeOffset? timestamp = null, CancellationToken cancellationToken = default, bool applyLocally = true)
    {
        var timestampValue = timestamp ?? DateTimeOffset.UtcNow;

        if (applyLocally || _broadcaster is null)
        {
            Timeline.ApplyEventStop(eventId, timestampValue);
        }

        if (_broadcaster is null)
        {
            return Task.CompletedTask;
        }

        var stop = new TimelineEventStop(eventId, timestampValue);
        return _broadcaster.SendStopAsync(stop, cancellationToken);
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

    private async Task RunDemoInternalAsync(CancellationToken cancellationToken)
    {
        var activeEvents = new HashSet<Guid>();

        async Task<Guid> StartEventAsync(string track, string label, Color color, Guid? parentId = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Guid.NewGuid();
            await BroadcastStartAsync(track, label, color, id, parentId, DateTimeOffset.UtcNow, CancellationToken.None, applyLocally: false).ConfigureAwait(false);
            activeEvents.Add(id);
            return id;
        }

        async Task StopEventAsync(Guid id)
        {
            if (activeEvents.Remove(id))
            {
                await BroadcastStopAsync(id, DateTimeOffset.UtcNow, CancellationToken.None, applyLocally: false).ConfigureAwait(false);
            }
        }

        try
        {
            var parentId = await StartEventAsync("Demo", "Demo Operation", Colors.SlateBlue).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);

            var ioId = await StartEventAsync("Demo", "Disk IO", Colors.OrangeRed, parentId).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

            var cpuId = await StartEventAsync("Demo", "CPU Work", Colors.SeaGreen, parentId).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken).ConfigureAwait(false);

            await StopEventAsync(ioId).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);

            var uiId = await StartEventAsync("Demo", "UI Thread", Colors.Gold, parentId).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(450), cancellationToken).ConfigureAwait(false);

            await StopEventAsync(cpuId).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);

            await StopEventAsync(uiId).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);

            await StopEventAsync(parentId).ConfigureAwait(false);

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(false);

            var backgroundId = await StartEventAsync("Background", "Telemetry", Colors.MediumOrchid).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(2000), cancellationToken).ConfigureAwait(false);
            await StopEventAsync(backgroundId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Demo cancelled by user; cleanup happens below.
        }
        finally
        {
            var remaining = new List<Guid>(activeEvents);
            foreach (var id in remaining)
            {
                activeEvents.Remove(id);
                await BroadcastStopAsync(id, DateTimeOffset.UtcNow, CancellationToken.None, applyLocally: false).ConfigureAwait(false);
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
}
