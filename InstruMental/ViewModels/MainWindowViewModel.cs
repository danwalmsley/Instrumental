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
using InstruMental.Services;
using ReactiveUI;
using System.Reactive.Linq;
using InstruMental.Contracts.Monitoring;

namespace InstruMental.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable, IDisposable
{
    private bool _disposed;
    private readonly List<IDisposable> _commandsToDispose = new();
    private readonly InstruMentalActivityListener? _metricListener;

    public MainWindowViewModel()
    {
        StartCaptureCommand = RegisterCommand(ReactiveCommand.Create(Timeline.StartCapture));
        StopCaptureCommand = CreateCommand(Timeline.StopCapture, () => Timeline.IsCapturing, nameof(TimelineViewModel.IsCapturing));
        ZoomInCommand = CreateCommand(() => Timeline.AdjustZoom(0.5));
        ZoomOutCommand = CreateCommand(() => Timeline.AdjustZoom(2.0));
        ResetZoomCommand = CreateCommand(Timeline.ResetZoom);
        GoLiveCommand = CreateCommand(Timeline.GoLive, () => !Timeline.IsLive, nameof(TimelineViewModel.IsLive));
        ClearCommand = CreateCommand(Timeline.ClearTimeline);

        // Reflect inner Timeline property changes outward for pass-through properties
        Timeline.PropertyChanged += OnTimelinePropertyChanged;

        if (Design.IsDesignMode)
        {
            SeedDesignTimeData();
        }
        else
        {
            // Listen to InstruMental activity stream (default localhost:5005)
            var opts = new InstruMentalActivityListenerOptions
            {
                ListenAddress = IPAddress.Loopback,
                Port = 5005
            };
            var listener = new InstruMentalActivityListener(opts);
            listener.ActivityReceived += OnActivityReceived;
            listener.MetricReceived += OnMetricReceived;
            listener.Start();
            _metricListener = listener;
        }
    }

    private void OnTimelinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(TimelineViewModel.TriggerHoldoffText))
        {
            OnPropertyChanged(nameof(TriggerHoldoffText));
        }
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(TimelineViewModel.TriggerMeasurePositionText))
        {
            OnPropertyChanged(nameof(TriggerMeasurePositionText));
        }
    }

    public string TriggerHoldoffText
    {
        get => Timeline.TriggerHoldoffText;
        set => Timeline.TriggerHoldoffText = value;
    }

    public string TriggerMeasurePositionText
    {
        get => Timeline.TriggerMeasurePositionText;
        set => Timeline.TriggerMeasurePositionText = value;
    }

    public TimelineViewModel Timeline { get; } = new();

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

    private void OnActivityReceived(ActivitySample activity)
    {
        // Map activity to a complete event on a single track.
        var track = "Avalonia Activities";
        var start = activity.StartTimestamp;
        var end = start + TimeSpan.FromMilliseconds(activity.DurationMilliseconds);
        var label = activity.Name;
        var color = ColorFromName(label);
        var id = Guid.NewGuid();

        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Timeline.ApplyCompleteEvent(id, track, start, end, label, color);
        }, global::Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnMetricReceived(MetricSample sample)
    {
        // Only map Avalonia duration metrics to timeline events
        if (string.IsNullOrEmpty(sample.MeterName) || !sample.MeterName.StartsWith("Avalonia", StringComparison.Ordinal))
        {
            return;
        }

        if (!double.IsFinite(sample.Value) || sample.Value < 0)
        {
            return;
        }

        // Accept if unit is ms, or if instrument name ends with .time (common for duration metrics)
        var name = sample.InstrumentName ?? string.Empty;
        var unitOk = !string.IsNullOrEmpty(sample.Unit) && sample.Unit.Equals("ms", StringComparison.OrdinalIgnoreCase);
        var looksLikeDuration = name.EndsWith(".time", StringComparison.OrdinalIgnoreCase);
        if (!(unitOk || looksLikeDuration))
        {
            return;
        }

        var track = ResolveTrackName(name) ?? ResolveFallbackTrack(name);
        if (track is null)
        {
            return; // unknown metric type -> ignore for timeline
        }

        var duration = TimeSpan.FromMilliseconds(sample.Value);
        var start = sample.Timestamp; // producer sets start for *.time (ms); otherwise treat as start
        var end = start + duration;
        if (duration == TimeSpan.Zero)
        {
            // Render as a tiny visible pulse
            end = start + TimeSpan.FromMilliseconds(0.25);
        }

        var label = name;
        var color = ColorFromName(label);
        var id = Guid.NewGuid();

        global::Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Timeline.ApplyCompleteEvent(id, track, start, end, label, color);
        }, global::Avalonia.Threading.DispatcherPriority.Background);
    }

    private static string? ResolveTrackName(string instrumentName)
    {
        if (string.IsNullOrEmpty(instrumentName)) return null;
        var name = instrumentName.ToLowerInvariant();
        return name switch
        {
            "avalonia.comp.render.time" => "Compositor Render",
            "avalonia.comp.update.time" => "Compositor Update",
            "avalonia.ui.measure.time" => "UI Measure",
            "avalonia.ui.arrange.time" => "UI Arrange",
            "avalonia.ui.render.time" => "UI Render",
            "avalonia.ui.input.time" => "UI Input",
            _ => null
        };
    }

    private static string? ResolveFallbackTrack(string instrumentName)
    {
        var name = instrumentName.ToLowerInvariant();
        if (name.Contains(".measure.")) return "UI Measure";
        if (name.Contains(".arrange.")) return "UI Arrange";
        if (name.Contains(".render.")) return "UI Render";
        if (name.Contains(".input.")) return "UI Input";
        if (name.Contains("comp.render")) return "Compositor Render";
        if (name.Contains("comp.update")) return "Compositor Update";
        return null;
    }

    private static Color ColorFromName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Colors.SteelBlue;
        }
        // Simple deterministic hash to color mapping
        unchecked
        {
            int hash = 17;
            foreach (var ch in name)
            {
                hash = hash * 31 + ch;
            }
            byte r = (byte)(hash & 0xFF);
            byte g = (byte)((hash >> 8) & 0xFF);
            byte b = (byte)((hash >> 16) & 0xFF);
            // Normalize to slightly brighter palette
            r = (byte)(128 + (r / 2));
            g = (byte)(128 + (g / 2));
            b = (byte)(128 + (b / 2));
            return Color.FromArgb(255, r, g, b);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var command in _commandsToDispose)
        {
            command.Dispose();
        }
        _commandsToDispose.Clear();

        Timeline.Dispose();

        if (_metricListener is not null)
        {
            await _metricListener.DisposeAsync();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

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
        return (T)(global::Avalonia.AvaloniaLocator.Current.GetService(typeof(T)) ?? throw new InvalidOperationException($"Service of type {typeof(T)} not found."));
    }
}
