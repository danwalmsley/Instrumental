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

    // Curated palette of dark, saturated (Material 800/900-style) colors that maintain contrast with white text.
    private static readonly Color[] s_colorPalette = new[]
    {
        Color.FromArgb(255, 183, 28, 28),  // #B71C1C - Red 900
        Color.FromArgb(255, 198, 40, 40),  // #C62828 - Red 800
        Color.FromArgb(255, 173, 20, 87),  // #AD1457 - Pink 800
        Color.FromArgb(255, 106, 27, 154), // #6A1B9A - Purple 800
        Color.FromArgb(255, 69, 39, 160),  // #4527A0 - Deep Purple 800
        Color.FromArgb(255, 13, 71, 161),  // #0D47A1 - Blue 900
        Color.FromArgb(255, 2, 70, 122),   // #02467A - Deep blue variant
        Color.FromArgb(255, 0, 77, 64),    // #004D40 - Teal 900
        Color.FromArgb(255, 0, 96, 100),   // #006064 - Teal 800
        Color.FromArgb(255, 27, 94, 32),   // #1B5E20 - Green 900
        Color.FromArgb(255, 46, 125, 50),  // #2E7D32 - Green 700 (kept)
        Color.FromArgb(255, 93, 64, 55),   // #5D4037 - Brown 700 (kept)
        Color.FromArgb(255, 69, 90, 100),  // #455A64 - Blue Grey 700 (kept)
        Color.FromArgb(255, 55, 71, 79),   // #37474F - Blue Grey 800 (kept)
        Color.FromArgb(255, 38, 50, 56),   // #263238 - Blue Grey 900
    };

    private static Color ColorFromName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return Colors.SteelBlue;
        }

        // Stable deterministic hash -> non-negative index into palette
        unchecked
        {
            int hash = 17;
            foreach (var ch in name)
            {
                hash = hash * 31 + ch;
            }

            // Cast to unsigned to ensure non-negative, then modulo palette length
            var idx = (int)((uint)hash % (uint)s_colorPalette.Length);
            return s_colorPalette[idx];
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
