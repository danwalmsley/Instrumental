using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Media;
using InstruMental.Diagnostics.Services;
using ReactiveUI;
using System.Reactive.Linq;
using InstruMental.Contracts.Monitoring;
using InstruMental.Contracts.Serialization;

namespace InstruMental.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable, IDisposable
{
    private bool _disposed;
    private readonly List<IDisposable> _commandsToDispose = new();
    private readonly UdpMetricsListener? _metricListener;

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
            var listener = new UdpMetricsListener(5005, EnvelopeEncoding.Binary, IPAddress.Loopback, null);
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
    }

    public string TriggerHoldoffText
    {
        get => Timeline.TriggerHoldoffText;
        set => Timeline.TriggerHoldoffText = value;
    }

    // Note: Trigger measure position is now controlled via slider bound to Timeline.TriggerMeasurePositionFraction
    // and displayed via Timeline.TriggerMeasurePositionDivText. No local proxy is required.

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
            // Compositor: group render and update on the same track
            "avalonia.comp.render.time" => "Compositor",
            "avalonia.comp.update.time" => "Compositor",

            // UI metrics grouped on single UI Thread track
            "avalonia.ui.measure.time" => "UI Thread",
            "avalonia.ui.arrange.time" => "UI Thread",
            "avalonia.ui.render.time" => "UI Thread",
            "avalonia.ui.input.time" => "UI Thread",
            _ => null
        };
    }

    private static string? ResolveFallbackTrack(string instrumentName)
    {
        var name = instrumentName.ToLowerInvariant();
        // Compositor variants
        if (name.Contains("comp.render") || name.Contains("compositor.render")) return "Compositor";
        if (name.Contains("comp.update") || name.Contains("compositor.update")) return "Compositor";

        // UI variants mapped to UI Thread
        if (name.Contains(".measure.")) return "UI Thread";
        if (name.Contains(".arrange.")) return "UI Thread";
        if (name.Contains(".render.")) return "UI Thread";
        if (name.Contains(".input.")) return "UI Thread";
        return null;
    }

    // Curated palette of bold, saturated colors. These are brighter/bolder than the previous dark palette
    // and intentionally chosen to be visually distinctive when cycled through.
    private static readonly Color[] s_colorPalette = new[]
    {
        Color.FromArgb(255, 244, 67, 54),   // #F44336 - Red 500
        Color.FromArgb(255, 255, 87, 34),   // #FF5722 - Deep Orange 500
        Color.FromArgb(255, 255, 193, 7),   // #FFC107 - Amber 500
        Color.FromArgb(255, 255, 235, 59),  // #FFEB3B - Yellow 500
        Color.FromArgb(255, 139, 195, 74),  // #8BC34A - Light Green 500
        Color.FromArgb(255, 76, 175, 80),   // #4CAF50 - Green 500
        Color.FromArgb(255, 0, 188, 212),   // #00BCD4 - Cyan 500
        Color.FromArgb(255, 3, 169, 244),   // #03A9F4 - Light Blue 500
        Color.FromArgb(255, 33, 150, 243),  // #2196F3 - Blue 500
        Color.FromArgb(255, 63, 81, 181),   // #3F51B5 - Indigo 500
        Color.FromArgb(255, 156, 39, 176),  // #9C27B0 - Purple 500
        Color.FromArgb(255, 233, 30, 99),   // #E91E63 - Pink 500
        Color.FromArgb(255, 255, 152, 0),   // #FF9800 - Orange 500
        Color.FromArgb(255, 121, 85, 72),   // #795548 - Brown 500
        Color.FromArgb(255, 96, 125, 139),  // #607D8B - Blue Grey 500
    };

    // Mapping of normalized name -> assigned palette color. We cycle through the palette on first-seen names
    // so the first distinct name gets palette[0], next gets palette[1], etc. The mapping is stable for the
    // lifetime of the app instance.
    private static readonly Dictionary<string, Color> s_nameToColor = new();
    private static int _sNextPaletteIndex;
    private static readonly object s_colorLock = new();

    private static Color ColorFromName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Colors.SteelBlue;
        }

        // Normalize to provide case-insensitive stable mapping
        var key = name.ToLowerInvariant();

        lock (s_colorLock)
        {
            if (s_nameToColor.TryGetValue(key, out var existing))
            {
                return existing;
            }

            // Assign next color in the palette and advance the index (wrap around)
            var color = s_colorPalette[_sNextPaletteIndex % s_colorPalette.Length];
            s_nameToColor[key] = color;
            _sNextPaletteIndex = (_sNextPaletteIndex + 1) % s_colorPalette.Length;
            return color;
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
