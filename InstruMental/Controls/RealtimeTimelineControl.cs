using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using InstruMental.Models;
using InstruMental.ViewModels;
using Avalonia.Threading;
using System.Threading;

namespace InstruMental.Controls;

public class RealtimeTimelineControl : Control
{
    private const double TrackLabelAreaHeight = 24;
    private const double LabelTextPadding = 2;
    private const double MinVerticalLabelFontSize = 8;
    private const double MaxVerticalLabelFontSize = 12;
    private const double SummaryHeight = 64;
    private const double SummarySpacing = 8;
    private const double SummaryTickLabelHeight = 16;
    private const double SummaryWindowMinWidth = 6;
    private const double OutsideLabelPadding = 4;
    private const double OutsideLabelGap = 2;
    private const double MinEventDrawWidth = 10; // Minimum on-screen width for an event

    private readonly Dictionary<TimelineTrack, NotifyCollectionChangedEventHandler?> _trackHandlers = new();
    private readonly Dictionary<TimelineEvent, NotifyCollectionChangedEventHandler> _eventChildHandlers = new();
    private readonly Dictionary<TimelineTrack, Rect> _trackBounds = new();
    private static readonly ImmutableSolidColorBrush TrackBackgroundBrush = new(Color.FromArgb(24, 255, 255, 255));
    private static readonly ImmutableSolidColorBrush LabelAreaBackgroundBrush = new(Color.FromArgb(12, 255, 255, 255));
    private static readonly ImmutableSolidColorBrush LabelTextBrush = new(Color.FromArgb(220, 255, 255, 255));
    private static readonly Pen TrackSeparatorPen = new(new ImmutableSolidColorBrush(Color.FromArgb(32, 255, 255, 255)), 1);
    private static readonly Pen GridPen = new(new ImmutableSolidColorBrush(Color.FromArgb(48, 255, 255, 255)), 1);
    private static readonly Pen CurrentTimePen = new(new ImmutableSolidColorBrush(Color.FromArgb(192, 255, 140, 0)), 2);
    private static readonly ImmutableSolidColorBrush SummaryBackgroundBrush = new(Color.FromArgb(255, 30, 30, 30));
    private static readonly Pen SummaryBorderPen = new(new ImmutableSolidColorBrush(Color.FromArgb(64, 255, 255, 255)), 1);
    private static readonly ImmutableSolidColorBrush SummaryWindowFill = new(Color.FromArgb(48, 255, 255, 255));
    private static readonly Pen SummaryWindowPen = new(new ImmutableSolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.5);
    private static readonly Pen SummaryTickPen = new(new ImmutableSolidColorBrush(Color.FromArgb(72, 255, 255, 255)), 1);
    private static readonly ImmutableSolidColorBrush SummaryTextBrush = new(Color.FromArgb(200, 255, 255, 255));
    private static readonly Pen MinorGridPen = new(new ImmutableSolidColorBrush(Color.FromArgb(24, 255, 255, 255)), 1);
    private static readonly ImmutableSolidColorBrush ScaleLabelBackgroundBrush = new(Color.FromArgb(128, 0, 0, 0));
    private const int OscilloscopeMajorDivisions = 10; // Standard number of time divisions across width
    private const int OscilloscopeMinorSubdivisions = 5; // Minor subdivisions per major division
    private static readonly Pen TriggerPen = new(new ImmutableSolidColorBrush(Color.FromArgb(224, 0, 200, 255)), 2);
    private static readonly Pen MeasurementPen = new(new ImmutableSolidColorBrush(Color.FromArgb(220, 255, 221, 109)), 1.5);
    private static readonly ImmutableSolidColorBrush MeasurementLabelBrush = new(Color.FromArgb(255, 255, 221, 109));
    private static readonly ImmutableSolidColorBrush MeasurementBackgroundBrush = new(Color.FromArgb(180, 35, 42, 56));
    private static readonly ImmutableSolidColorBrush MeasurementHighlightBrush = new(Color.FromArgb(40, 255, 221, 109));
    private static readonly Pen MeasurementBorderPen = new(MeasurementLabelBrush, 1);

    private Rect _summaryBounds;
    private Rect _summaryWindowBounds;
    private bool _isPanning;
    private bool _isSummaryDragging;
    private Point _lastPointerPosition;
    private double _summaryDragOffsetRatio;
    private Point? _lastHoverPosition;
    private readonly List<EventVisual> _eventVisuals = new();
    private EventHoverMeasurement? _hoverMeasurement;
    private RenderContext? _lastRenderContext;

    public static readonly StyledProperty<IList<TimelineTrack>?> TracksProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, IList<TimelineTrack>?>(nameof(Tracks));

    public static readonly StyledProperty<DateTimeOffset> CurrentTimeProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, DateTimeOffset>(nameof(CurrentTime), DateTimeOffset.UtcNow);

    public static readonly StyledProperty<TimeSpan> VisibleDurationProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, TimeSpan>(nameof(VisibleDuration), TimeSpan.FromSeconds(30));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, IBrush?>(nameof(Background));

    public static readonly StyledProperty<TimelineViewModel?> TimelineProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, TimelineViewModel?>(nameof(Timeline));

    public static readonly StyledProperty<double> InfoBarHeightProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, double>(nameof(InfoBarHeight), 24d);

    static RealtimeTimelineControl()
    {
        AffectsRender<RealtimeTimelineControl>(TracksProperty, CurrentTimeProperty, VisibleDurationProperty);
    }

    public RealtimeTimelineControl()
    {
        ClipToBounds = true;
    }

    public IList<TimelineTrack>? Tracks
    {
        get => GetValue(TracksProperty);
        set
        {
            var oldValue = Tracks;
            SetValue(TracksProperty, value);

            if (!ReferenceEquals(oldValue, value))
            {
                DetachTrackHandlers(oldValue);
                AttachTrackHandlers(value);
                QueueInvalidateVisual();
            }
        }
    }

    public DateTimeOffset CurrentTime
    {
        get => GetValue(CurrentTimeProperty);
        set => SetValue(CurrentTimeProperty, value);
    }

    public TimeSpan VisibleDuration
    {
        get => GetValue(VisibleDurationProperty);
        set => SetValue(VisibleDurationProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public TimelineViewModel? Timeline
    {
        get => GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    public double InfoBarHeight
    {
        get => GetValue(InfoBarHeightProperty);
        set => SetValue(InfoBarHeightProperty, value);
    }

    private void AttachTimelineHandlers(TimelineViewModel? timeline)
    {
        if (timeline is null) return;
        timeline.PropertyChanged -= TimelineOnPropertyChanged;
        timeline.PropertyChanged += TimelineOnPropertyChanged;
    }

    private void DetachTimelineHandlers(TimelineViewModel? timeline)
    {
        if (timeline is null) return;
        timeline.PropertyChanged -= TimelineOnPropertyChanged;
    }

    private void TimelineOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is nameof(TimelineViewModel.TriggerEnabled) or
            nameof(TimelineViewModel.LastTriggerTime) or
            nameof(TimelineViewModel.VisibleDuration) or
            nameof(TimelineViewModel.ViewportEnd) or
            nameof(TimelineViewModel.TriggerMeasurePosition))
        {
            QueueInvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TimelineProperty)
        {
            var oldVm = change.GetOldValue<TimelineViewModel?>();
            var newVm = change.GetNewValue<TimelineViewModel?>();
            if (!ReferenceEquals(oldVm, newVm))
            {
                DetachTimelineHandlers(oldVm);
                AttachTimelineHandlers(newVm);
                QueueInvalidateVisual();
            }
        }
    }

    private void AttachTrackHandlers(IList<TimelineTrack>? tracks)
    {
        if (tracks is null)
        {
            return;
        }

        if (tracks is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += TracksOnCollectionChanged;
        }

        foreach (var track in tracks)
        {
            AttachTrack(track);
        }
    }

    private void DetachTrackHandlers(IList<TimelineTrack>? tracks)
    {
        if (tracks is null)
        {
            return;
        }

        if (tracks is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged -= TracksOnCollectionChanged;
        }

        foreach (var track in tracks)
        {
            DetachTrack(track);
        }
    }

    private void TracksOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var handler in _trackHandlers.Keys.ToList())
            {
                DetachTrack(handler);
            }
        }
        else
        {
            if (e.OldItems is not null)
            {
                foreach (var item in e.OldItems.OfType<TimelineTrack>())
                {
                    DetachTrack(item);
                }
            }

            if (e.NewItems is not null)
            {
                foreach (var item in e.NewItems.OfType<TimelineTrack>())
                {
                    AttachTrack(item);
                }
            }
        }

        QueueInvalidateVisual();
    }

    private void AttachTrack(TimelineTrack track)
    {
        if (_trackHandlers.ContainsKey(track))
        {
            // If the Events collection has changed, re-attach handler
            if (_trackHandlers[track] != null && track.Events is INotifyCollectionChanged notifyOld)
            {
                notifyOld.CollectionChanged -= _trackHandlers[track];
            }
        }

        void Handler(object? _, NotifyCollectionChangedEventArgs args)
        {
            if (args.OldItems is not null)
            {
                foreach (var evt in args.OldItems.OfType<TimelineEvent>())
                {
                    DetachEvent(evt);
                }
            }
            if (args.NewItems is not null)
            {
                foreach (var evt in args.NewItems.OfType<TimelineEvent>())
                {
                    AttachEvent(evt);
                }
            }
            QueueInvalidateVisual();
        }

        // Always attach handler to the current Events collection
        if (track.Events is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += Handler;
            _trackHandlers[track] = Handler;
        }
        else
        {
            _trackHandlers[track] = null;
        }

        foreach (var evt in track.Events)
        {
            AttachEvent(evt);
        }

        // Listen for Events property changes if TimelineTrack supports it
        if (track is System.ComponentModel.INotifyPropertyChanged npc)
        {
            npc.PropertyChanged -= TrackOnPropertyChanged;
            npc.PropertyChanged += TrackOnPropertyChanged;
        }
    }

    private void DetachTrack(TimelineTrack track)
    {
        if (!_trackHandlers.TryGetValue(track, out var handler))
        {
            return;
        }
        if (handler is not null && track.Events is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged -= handler;
        }
        foreach (var evt in track.Events)
        {
            DetachEvent(evt);
        }
        if (track is System.ComponentModel.INotifyPropertyChanged npc)
        {
            npc.PropertyChanged -= TrackOnPropertyChanged;
        }
        _trackHandlers.Remove(track);
    }

    private void TrackOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is TimelineTrack track && e.PropertyName == nameof(TimelineTrack.Events))
        {
            // Detach old handler and attach new one
            DetachTrack(track);
            AttachTrack(track);
            QueueInvalidateVisual();
        }
    }

    private void EventOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        QueueInvalidateVisual();
    }

    // Ensures InvalidateVisual runs on the UI thread. Some event sources add
    // events from background threads; posting to the UI thread ensures safe
    // redraws and keeps the timeline responsive while avoiding cross-thread
    // exceptions.
    private int _invalidatePending;
    private void QueueInvalidateVisual()
    {
        // If we're already on the UI thread just invalidate immediately.
        if (Dispatcher.UIThread.CheckAccess())
        {
            InvalidateVisual();
            return;
        }

        // Coalesce multiple background requests into a single posted action.
        if (Interlocked.Exchange(ref _invalidatePending, 1) == 1)
        {
            return; // already queued
        }

        Dispatcher.UIThread.Post(() =>
        {
            // Clear pending flag and invalidate.
            Interlocked.Exchange(ref _invalidatePending, 0);
            InvalidateVisual();
        }, DispatcherPriority.Normal);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var now = CurrentTime;
        var visibleDuration = VisibleDuration;
        if (visibleDuration <= TimeSpan.Zero)
        {
            visibleDuration = TimeSpan.FromSeconds(1);
        }

        var windowStart = now - visibleDuration;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var background = this.Background ?? Brushes.Transparent;
        context.DrawRectangle(background, null, new Rect(Bounds.Size));

    _eventVisuals.Clear();
    _trackBounds.Clear();
    _lastRenderContext = null;

        // Total content height excluding summary section
        var contentHeight = Math.Max(0, height - SummaryHeight - SummarySpacing);
        var desiredInfoBarHeight = Math.Max(0, InfoBarHeight);
        var infoBarHeight = Math.Min(desiredInfoBarHeight, contentHeight); // clamp if very small
        var tracksContentHeight = Math.Max(0, contentHeight - infoBarHeight);

        _lastRenderContext = new RenderContext(windowStart, now, width, tracksContentHeight);

        // Draw time grid only over track content area (not info bar)
        DrawTimeGrid(context, windowStart, now, visibleDuration, width, tracksContentHeight);

        // Layout tracks inside tracksContentHeight
        var tracks = Tracks;
        var trackCount = tracks?.Count ?? 0;
        if (trackCount > 0 && tracks is not null && tracksContentHeight > 0)
        {
            var totalLabelHeight = TrackLabelAreaHeight * trackCount;
            var trackAreaHeight = Math.Max(0, tracksContentHeight - totalLabelHeight);
            var trackHeight = trackCount > 0 ? trackAreaHeight / trackCount : 0;
            var stride = trackHeight + TrackLabelAreaHeight;
            var top = 0d;

            for (var index = 0; index < trackCount; index++)
            {
                var track = tracks[index];
                var trackBounds = new Rect(0, top, width, trackHeight);
                var labelArea = new Rect(0, trackBounds.Bottom, width, TrackLabelAreaHeight);
                _trackBounds[track] = trackBounds;
                DrawTrack(context, track, windowStart, now, width, trackBounds, labelArea);
                top += stride;
            }
        }

        // Draw trigger indicator across tracks area
        DrawTriggerIndicator(context, width, tracksContentHeight);

        // Current time indicator spans only tracks+labels, not info bar
        DrawCurrentTimeIndicator(context, windowStart, now, width, tracksContentHeight);

        EventHoverMeasurement? measurement = null;
        if (_lastHoverPosition is { } hoverPosition && _lastRenderContext is { } renderContext)
        {
            measurement = ComputeHoverMeasurement(hoverPosition, renderContext);
        }

        _hoverMeasurement = measurement;
        if (measurement is { } resolvedMeasurement)
        {
            DrawHoverMeasurement(context, resolvedMeasurement);
        }

        // Info bar rectangle directly after tracks content
        var infoBarTop = tracksContentHeight;
        var infoBarRect = new Rect(0, infoBarTop, width, infoBarHeight);
        DrawInfoBar(context, infoBarRect, TimeSpan.FromTicks(visibleDuration.Ticks / OscilloscopeMajorDivisions));

        // Summary below full content area (tracks + info bar)
        var summaryTop = contentHeight + SummarySpacing; // contentHeight already includes info bar
        var summaryBounds = new Rect(0, summaryTop, width, Math.Max(0, height - summaryTop));
        _summaryBounds = summaryBounds;
        DrawSummaryTimeline(context, summaryBounds, windowStart, now);
    }

    private void DrawTriggerIndicator(DrawingContext context, double width, double height)
    {
        var vm = Timeline;
        if (vm is null || !vm.TriggerEnabled || width <= 0 || height <= 0)
        {
            return;
        }

        // Compute x from measure position (time) relative to center
        var vis = VisibleDuration;
        if (vis <= TimeSpan.Zero)
        {
            vis = TimeSpan.FromMilliseconds(1);
        }
        var half = TimeSpan.FromTicks(vis.Ticks / 2);
        var measure = vm.TriggerMeasurePosition;
        if (measure > half) measure = half;
        if (measure < -half) measure = -half;
        var centerX = width / 2.0;
        var ratio = half.Ticks > 0 ? (double)measure.Ticks / half.Ticks : 0.0; // -1..1
        var x = centerX + ratio * centerX;
        x = Math.Clamp(x, 0, width);

        context.DrawLine(TriggerPen, new Point(x, 0), new Point(x, height));

        // Optional small top indicator triangle
        var triHeight = 8.0;
        var triHalf = 6.0;
        var p1 = new Point(x, 0);
        var p2 = new Point(x - triHalf, triHeight);
        var p3 = new Point(x + triHalf, triHeight);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(p1, true);
            ctx.LineTo(p2);
            ctx.LineTo(p3);
            ctx.EndFigure(true);
        }
        context.DrawGeometry(new ImmutableSolidColorBrush(Color.FromArgb(160, 0, 200, 255)), null, geo);
    }

    private void DrawTimeGrid(DrawingContext context, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeSpan visibleDuration, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Oscilloscope-style: fixed number of major divisions across the width
        double majorDivisionWidth = width / OscilloscopeMajorDivisions;
        var perDivisionTime = TimeSpan.FromTicks(visibleDuration.Ticks / OscilloscopeMajorDivisions);

        // Draw major division lines
        for (int i = 0; i <= OscilloscopeMajorDivisions; i++)
        {
            double x = i * majorDivisionWidth;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
            if (i < OscilloscopeMajorDivisions)
            {
                // Minor subdivisions
                double minorWidth = majorDivisionWidth / OscilloscopeMinorSubdivisions;
                for (int m = 1; m < OscilloscopeMinorSubdivisions; m++)
                {
                    double mx = x + m * minorWidth;
                    context.DrawLine(MinorGridPen, new Point(mx, 0), new Point(mx, height));
                }
            }
        }
    }


    private void DrawInfoBar(DrawingContext context, Rect bounds, TimeSpan perDivisionTime)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        // Background
        context.DrawRectangle(LabelAreaBackgroundBrush, null, bounds);
        context.DrawLine(TrackSeparatorPen, new Point(bounds.Left, bounds.Top), new Point(bounds.Right, bounds.Top));

        // Scale label bottom-right inside this bar
        var scaleLabel = FormatTimeScalePerDivision(perDivisionTime) + "/div";
        var formatted = new FormattedText(
            scaleLabel,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
            12,
            SummaryTextBrush)
        {
            MaxTextWidth = Math.Min(240, bounds.Width - 8),
            MaxTextHeight = bounds.Height - 4
        };
        const double margin = 4;
        var labelRect = new Rect(
            Math.Max(bounds.X + margin, bounds.Right - formatted.Width - 8 - margin),
            bounds.Top + (bounds.Height - (formatted.Height + 4)) / 2,
            formatted.Width + 8,
            formatted.Height + 4);
        context.DrawRectangle(ScaleLabelBackgroundBrush, null, labelRect);
        context.DrawText(formatted, new Point(labelRect.X + 4, labelRect.Y + 2));
    }

    private void DrawTrack(DrawingContext context, TimelineTrack track, DateTimeOffset windowStart, DateTimeOffset windowEnd, double width, Rect trackBounds, Rect labelArea)
    {
        context.DrawRectangle(TrackBackgroundBrush, null, trackBounds);
        context.DrawLine(TrackSeparatorPen, new Point(trackBounds.Left, trackBounds.Bottom), new Point(trackBounds.Right, trackBounds.Bottom));

        if (labelArea.Height > 0)
        {
            context.DrawRectangle(LabelAreaBackgroundBrush, null, labelArea);
            context.DrawLine(TrackSeparatorPen, new Point(labelArea.Left, labelArea.Bottom), new Point(labelArea.Right, labelArea.Bottom));

            // Draw track name centered vertically, left aligned
            if (!string.IsNullOrWhiteSpace(track.Name))
            {
                var formatted = new FormattedText(
                    track.Name,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold),
                    12,
                    LabelTextBrush)
                {
                    MaxTextWidth = labelArea.Width - 8,
                    MaxTextHeight = labelArea.Height - 4,
                    TextAlignment = TextAlignment.Left
                };
                var textY = labelArea.Y + (labelArea.Height - formatted.Height) / 2;
                context.DrawText(formatted, new Point(labelArea.X + 4, textY));
            }
        }

        if (track.Events.Count == 0)
        {
            return;
        }

        // Apply level-of-detail filtering based on zoom level and event count
        var eventsToRender = GetEventsForLevelOfDetail(track.Events, windowStart, windowEnd, width);
        TimelineEvent? prev = null;
        TimelineEvent? next = null;
        var eventsList = eventsToRender as IList<TimelineEvent> ?? eventsToRender.ToList();
        int count = eventsList.Count;
        for (int i = 0; i < count; i++)
        {
            var evt = eventsList[i];
            double? prevRight = null;
            if (i > 0)
            {
                prev = eventsList[i - 1];
                var prevEnd = prev.End ?? windowEnd;
                prevRight = TimeToX(prevEnd, windowStart, windowEnd, width);
            }
            double? nextLeft = null;
            if (i < count - 1)
            {
                next = eventsList[i + 1];
                nextLeft = TimeToX(next.Start, windowStart, windowEnd, width);
            }
            DrawEvent(context, track, evt, windowStart, windowEnd, width, trackBounds, trackBounds, labelArea, 0, prevRight, nextLeft);
        }
    }

    // Level-of-detail constants
    private const int MaxEventsToRender = 100;
    private const int FullDetailThreshold = 50;

    /// <summary>
    /// Gets a filtered list of events to render based on the current zoom level and viewport.
    /// When there are many events, this applies level-of-detail filtering to maintain performance.
    /// At detailed zoom levels (50ms tick spacing or less), shows all visible events without sampling.
    /// </summary>
    private IEnumerable<TimelineEvent> GetEventsForLevelOfDetail(IList<TimelineEvent> allEvents, DateTimeOffset windowStart, DateTimeOffset windowEnd, double viewportWidth)
    {
        // Calculate the current zoom level based on visible duration
        var visibleDuration = windowEnd - windowStart;
        var totalSeconds = visibleDuration.TotalSeconds;

        // Calculate what the grid step would be at this zoom level
        var targetLines = (int)Math.Clamp(totalSeconds, 10, 60);
        var gridStepSeconds = CalculateGridStep(totalSeconds, targetLines);

        // If grid lines represent 50ms or less, we're at high detail - show all visible events
        const double detailThresholdSeconds = 0.05; // 50ms
        if (gridStepSeconds <= detailThresholdSeconds)
        {
            // At high zoom levels, only filter by visibility - no sampling
            return allEvents.Where(evt => IsEventVisible(evt, windowStart, windowEnd));
        }

        // At lower zoom levels, apply the existing LOD logic
        if (allEvents.Count <= FullDetailThreshold)
        {
            // If we have few events, render them all
            return allEvents.Where(evt => IsEventVisible(evt, windowStart, windowEnd));
        }

        // Filter to only events that are visible in the current viewport
        var visibleEvents = allEvents.Where(evt => IsEventVisible(evt, windowStart, windowEnd)).ToList();

        if (visibleEvents.Count <= MaxEventsToRender)
        {
            // If visible events are within our limit, render them all
            return visibleEvents;
        }

        // Apply level-of-detail sampling
        return ApplyLevelOfDetailSampling(visibleEvents, windowStart, windowEnd, viewportWidth);
    }

    /// <summary>
    /// Checks if an event is visible within the current viewport time range
    /// </summary>
    private static bool IsEventVisible(TimelineEvent evt, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var eventEnd = evt.End ?? windowEnd;
        return evt.Start < windowEnd && eventEnd > windowStart;
    }

    /// <summary>
    /// Applies intelligent sampling to reduce the number of events to render while maintaining visual representation.
    /// Since we now use complete events (with both start and end timestamps), all events are complete when broadcast.
    /// </summary>
    private IEnumerable<TimelineEvent> ApplyLevelOfDetailSampling(List<TimelineEvent> visibleEvents, DateTimeOffset windowStart, DateTimeOffset windowEnd, double viewportWidth)
    {
        // Calculate how many pixels each event would need to be clearly visible
        const double minPixelsPerEvent = 2.0; // Minimum pixels needed to see an event
        var maxEventsForPixelDensity = (int)(viewportWidth / minPixelsPerEvent);
        var targetEventCount = Math.Min(MaxEventsToRender, Math.Max(10, maxEventsForPixelDensity));

        if (visibleEvents.Count <= targetEventCount)
        {
            return visibleEvents;
        }

        // With complete events, we can sample all events equally since they all have end times
        // Sort events by start time for consistent sampling
        var sortedEvents = visibleEvents.OrderBy(evt => evt.Start).ToList();

        // Reserve slots for recent events to maintain real-time context
        var recentEventCount = Math.Min(targetEventCount / 4, Math.Min(10, sortedEvents.Count / 10));
        var recentEvents = sortedEvents.TakeLast(recentEventCount).ToList();

        // Calculate remaining slots for systematic sampling
        var remainingSlotsForSampling = targetEventCount - recentEventCount;
        var eventsToSample = sortedEvents.Take(sortedEvents.Count - recentEventCount).ToList();

        var sampledEvents = new List<TimelineEvent>();

        if (remainingSlotsForSampling > 0 && eventsToSample.Count > 0)
        {
            if (eventsToSample.Count <= remainingSlotsForSampling)
            {
                sampledEvents.AddRange(eventsToSample);
            }
            else
            {
                // Use systematic sampling to get a representative subset
                var samplingRatio = (double)eventsToSample.Count / remainingSlotsForSampling;

                for (int i = 0; i < remainingSlotsForSampling && i < eventsToSample.Count; i++)
                {
                    var index = (int)(i * samplingRatio);
                    if (index < eventsToSample.Count)
                    {
                        sampledEvents.Add(eventsToSample[index]);
                    }
                }
            }
        }

        // Combine sampled events with recent events, removing any duplicates, and sort by start time
        return sampledEvents.Concat(recentEvents).Distinct().OrderBy(evt => evt.Start);
    }

    private void DrawEvent(DrawingContext context, TimelineTrack track, TimelineEvent timelineEvent, DateTimeOffset windowStart, DateTimeOffset windowEnd, double width, Rect trackBounds, Rect availableBounds, Rect labelArea, int depth, double? prevEventRight = null, double? nextEventLeft = null)
    {
        // Check initial visibility against the window
        var eventEnd = timelineEvent.End ?? windowEnd;
        if (eventEnd <= windowStart || timelineEvent.Start >= windowEnd) return;
        var startX = TimeToX(timelineEvent.Start, windowStart, windowEnd, width);
        var endX = TimeToX(eventEnd, windowStart, windowEnd, width);
        if (endX <= startX) endX = startX + 1;
        startX = Math.Clamp(startX, availableBounds.Left, availableBounds.Right);
        endX = Math.Clamp(endX, availableBounds.Left, availableBounds.Right);
        var availableWidth = endX - startX;
        if (availableWidth <= 0) return;

        const double margin = 2;
        const double minInnerHeight = 4;
        // New hierarchical height scaling: each depth reduces height by factor (power curve) and centers inside parent
        double heightFactor = depth == 0 ? 1.0 : Math.Pow(0.6, depth); // 60% of parent for depth 1, 36% for depth 2, etc.
        double targetHeight = Math.Max(minInnerHeight, availableBounds.Height * heightFactor - margin * 2);
        double top = availableBounds.Top + (availableBounds.Height - targetHeight) / 2.0; // center vertically inside container

        var innerWidth = availableWidth - margin * 2;
        if (innerWidth < MinEventDrawWidth)
        {
            var expansion = MinEventDrawWidth - innerWidth;
            var newRight = startX + margin + innerWidth + expansion + margin;
            var maxRight = availableBounds.Right;
            if (newRight > maxRight)
            {
                var overflow = newRight - maxRight;
                startX = Math.Max(availableBounds.Left, startX - overflow);
            }
            innerWidth = MinEventDrawWidth;
            endX = startX + innerWidth + margin * 2;
        }

        var rect = new Rect(startX + margin, top + margin, innerWidth, targetHeight);
        var clampedEnd = timelineEvent.End ?? windowEnd;
        if (clampedEnd < timelineEvent.Start)
        {
            clampedEnd = timelineEvent.Start;
        }
        _eventVisuals.Add(new EventVisual(track, timelineEvent, rect, trackBounds, timelineEvent.Start, clampedEnd, depth));
        var roundedRect = new RoundedRect(rect, timelineEvent.CornerRadius);
        context.DrawRectangle(timelineEvent.Fill, null, roundedRect);

        var eventDuration = (timelineEvent.End ?? windowEnd) - timelineEvent.Start;
        if (eventDuration < TimeSpan.Zero) eventDuration = TimeSpan.Zero;
        var durationTextRaw = FormatDuration(eventDuration);

        // Pre-create formatted objects (full width & constrained variants)
        FormattedText CreateText(string text, double fontSize, double? maxWidth = null, bool ellipsis = false) => new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            fontSize,
            Brushes.White)
        {
            MaxTextWidth = maxWidth.HasValue ? Math.Max(0, maxWidth.Value) : double.PositiveInfinity,
            MaxTextHeight = double.PositiveInfinity,
            Trimming = ellipsis ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            TextAlignment = TextAlignment.Left
        };

        var fullLabel = !string.IsNullOrWhiteSpace(timelineEvent.Label) ? CreateText(timelineEvent.Label, 12) : null;
        var constrainedLabel = fullLabel != null ? CreateText(timelineEvent.Label, 12, rect.Width - 8, ellipsis: true) : null;
        var durationFormatted = CreateText(durationTextRaw, 10, rect.Width - 8);

        var labelDrawn = false;
        var durationDrawn = false;

        // Decide label strategy
        if (!string.IsNullOrWhiteSpace(timelineEvent.Label))
        {
            // Condition for attempting outside label: the full label does not fit inside the rect with padding
            var insideCanFit = fullLabel!.Width + 8 <= rect.Width && fullLabel.Height + durationFormatted.Height + 12 <= rect.Height && rect.Width >= 20;
            if (insideCanFit)
            {
                // Standard inside rendering
                var textOrigin = new Point(rect.X + 4, rect.Y + 4);
                context.DrawText(fullLabel, textOrigin);
                var durationOriginY = textOrigin.Y + fullLabel.Height + 2;
                if (durationOriginY + durationFormatted.Height > rect.Bottom - 4)
                    durationOriginY = rect.Bottom - durationFormatted.Height - 4;
                context.DrawText(durationFormatted, new Point(rect.X + 4, durationOriginY));
                labelDrawn = true;
                durationDrawn = true;
            }
            else
            {
                // Attempt right outside label if gap is sufficient and won't collide with next event
                bool placedOutside = false;
                if (fullLabel != null)
                {
                    var gapRightLimit = nextEventLeft ?? availableBounds.Right;
                    var neededRight = rect.Right + OutsideLabelPadding + fullLabel.Width;
                    if (neededRight <= gapRightLimit - 1 && neededRight <= availableBounds.Right)
                    {
                        var rightOrigin = new Point(rect.Right + OutsideLabelPadding, rect.Y + 4);
                        context.DrawText(fullLabel, rightOrigin);
                        var durOutside = CreateText(durationTextRaw, 10);
                        var durOrigin = new Point(rightOrigin.X, rightOrigin.Y + fullLabel.Height + OutsideLabelGap);
                        if (durOrigin.Y + durOutside.Height <= availableBounds.Bottom - 2)
                        {
                            context.DrawText(durOutside, durOrigin);
                            durationDrawn = true;
                        }
                        labelDrawn = true;
                        placedOutside = true;
                    }
                    if (!placedOutside)
                    {
                        // Attempt left outside
                        var gapLeftLimit = prevEventRight ?? availableBounds.Left;
                        var neededLeft = rect.Left - OutsideLabelPadding - fullLabel.Width;
                        if (neededLeft >= gapLeftLimit + 1 && neededLeft >= availableBounds.Left)
                        {
                            var leftOrigin = new Point(neededLeft, rect.Y + 4);
                            context.DrawText(fullLabel, leftOrigin);
                            var durOutside = CreateText(durationTextRaw, 10);
                            var durOrigin = new Point(leftOrigin.X, leftOrigin.Y + fullLabel.Height + OutsideLabelGap);
                            if (durOrigin.Y + durOutside.Height <= availableBounds.Bottom - 2)
                            {
                                context.DrawText(durOutside, durOrigin);
                                durationDrawn = true;
                            }
                            labelDrawn = true;
                            placedOutside = true;
                        }
                    }
                }
                if (!labelDrawn)
                {
                    if (rect.Width > 20 && constrainedLabel != null)
                    {
                        var textOrigin = new Point(rect.X + 4, rect.Y + 4);
                        context.DrawText(constrainedLabel, textOrigin);
                        var durationOriginY = textOrigin.Y + constrainedLabel.Height + 2;
                        if (durationOriginY + durationFormatted.Height > rect.Bottom - 4)
                            durationOriginY = rect.Bottom - durationFormatted.Height - 4;
                        context.DrawText(durationFormatted, new Point(rect.X + 4, durationOriginY));
                        labelDrawn = true;
                        durationDrawn = true;
                    }
                    else if (labelArea.Height > 0)
                    {
                        DrawVerticalLabel(context, timelineEvent.Label, rect.X + rect.Width / 2, labelArea);
                        DrawDurationInLabelArea(context, durationFormatted, rect, labelArea);
                        labelDrawn = true;
                        durationDrawn = true;
                    }
                }
            }
        }
        if (!labelDrawn)
        {
            if (rect.Width > 20)
            {
                var durationOrigin = new Point(rect.X + 4, rect.Bottom - durationFormatted.Height - 4);
                context.DrawText(durationFormatted, durationOrigin);
                durationDrawn = true;
            }
            else if (labelArea.Height > 0 && !durationDrawn)
            {
                DrawDurationInLabelArea(context, durationFormatted, rect, labelArea);
                durationDrawn = true;
            }
        }
        foreach (var child in timelineEvent.Children)
        {
            DrawEvent(context, track, child, windowStart, windowEnd, width, trackBounds, rect, labelArea, depth + 1);
        }
    }

    private void DrawDurationInLabelArea(DrawingContext context, FormattedText durationText, Rect eventRect, Rect labelArea)
    {
        if (labelArea.Height <= 0)
        {
            return;
        }

        var x = Math.Clamp(eventRect.X + eventRect.Width / 2 - durationText.Width / 2, labelArea.Left, labelArea.Right - durationText.Width);
        var y = labelArea.Bottom - durationText.Height - LabelTextPadding;
        context.DrawText(durationText, new Point(x, y));
    }

    private void DrawVerticalLabel(DrawingContext context, string text, double centerX, Rect labelArea)
    {
        if (string.IsNullOrWhiteSpace(text) || labelArea.Height <= LabelTextPadding * 2)
        {
            return;
        }

        var availableHeight = labelArea.Height - LabelTextPadding * 2;
        if (availableHeight <= 0)
        {
            return;
        }

        var characters = text.ToCharArray();
        var characterCount = characters.Length;
        if (characterCount == 0)
        {
            return;
        }

        var incrementalHeight = availableHeight / characterCount;
        var fontSize = Math.Clamp(incrementalHeight, MinVerticalLabelFontSize, MaxVerticalLabelFontSize);
        var typeface = new Typeface("Segoe UI");

        var y = labelArea.Top + LabelTextPadding;
        foreach (var ch in characters)
        {
            if (y > labelArea.Bottom)
            {
                break;
            }

            var glyph = new FormattedText(
                ch.ToString(),
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                LabelTextBrush);

            var origin = new Point(centerX - glyph.Width / 2, y);
            context.DrawText(glyph, origin);
            y += glyph.Height;
        }
    }

    private void DrawSummaryTimeline(DrawingContext context, Rect bounds, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        context.DrawRectangle(SummaryBackgroundBrush, SummaryBorderPen, bounds);

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            _summaryWindowBounds = new Rect();
            return;
        }

        var timeline = Timeline;
        if (timeline is null)
        {
            _summaryWindowBounds = new Rect();
            return;
        }

        var boundsInfo = timeline.GetTimelineBounds();
        if (boundsInfo.End <= boundsInfo.Start)
        {
            _summaryWindowBounds = bounds;
            return;
        }

        var eventAreaHeight = Math.Max(0, bounds.Height - SummaryTickLabelHeight);
        var eventArea = new Rect(bounds.X, bounds.Y, bounds.Width, eventAreaHeight);
        DrawSummaryEvents(context, eventArea, boundsInfo);
        DrawSummaryTicks(context, bounds, boundsInfo);

        var windowRect = CalculateSummaryWindow(bounds, boundsInfo, windowStart, windowEnd);
        _summaryWindowBounds = windowRect;
        context.DrawRectangle(SummaryWindowFill, SummaryWindowPen, windowRect);
    }

    private void DrawSummaryEvents(DrawingContext context, Rect eventArea, (DateTimeOffset Start, DateTimeOffset End) boundsInfo)
    {
        if (eventArea.Width <= 0 || eventArea.Height <= 0)
        {
            return;
        }

        var tracks = Tracks;
        if (tracks is null)
        {
            return;
        }

        foreach (var evt in EnumerateEvents(tracks))
        {
            var evtEnd = evt.End ?? boundsInfo.End;
            if (evtEnd <= boundsInfo.Start || evt.Start >= boundsInfo.End)
            {
                continue;
            }

            var startX = MapTimeToSummary(evt.Start, boundsInfo, eventArea);
            var endX = MapTimeToSummary(evtEnd, boundsInfo, eventArea);
            var width = Math.Max(1, endX - startX);
            var rect = new Rect(startX, eventArea.Y + 3, width, Math.Max(2, eventArea.Height - 6));
            var brush = evt.Fill ?? Brushes.White;
            context.DrawRectangle(brush, null, rect);
        }
    }

    private void DrawSummaryTicks(DrawingContext context, Rect bounds, (DateTimeOffset Start, DateTimeOffset End) range)
    {
        var totalSeconds = (range.End - range.Start).TotalSeconds;
        if (totalSeconds <= 0)
        {
            return;
        }

        var tickAreaTop = bounds.Bottom - SummaryTickLabelHeight;
        var tickStep = CalculateGridStep(totalSeconds, 8);
        var firstTickSeconds = Math.Ceiling(range.Start.ToUnixTimeMilliseconds() / (tickStep * 1000)) * tickStep;

        for (var tickSeconds = firstTickSeconds; tickSeconds <= range.End.ToUnixTimeMilliseconds() / 1000.0; tickSeconds += tickStep)
        {
            var tickTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(tickSeconds * 1000));
            var x = MapTimeToSummary(tickTime, range, bounds);
            context.DrawLine(SummaryTickPen, new Point(x, bounds.Top), new Point(x, tickAreaTop));

            var text = tickTime.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                SummaryTextBrush)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = 80,
                MaxTextHeight = SummaryTickLabelHeight
            };

            var textOrigin = new Point(x - formatted.Width / 2, tickAreaTop + (SummaryTickLabelHeight - formatted.Height) / 2);
            context.DrawText(formatted, textOrigin);
        }
    }

    private Rect CalculateSummaryWindow(Rect summaryBounds, (DateTimeOffset Start, DateTimeOffset End) range, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        var start = MapTimeToSummary(windowStart, range, summaryBounds);
        var end = MapTimeToSummary(windowEnd, range, summaryBounds);
        var width = Math.Max(SummaryWindowMinWidth, end - start);
        start = Math.Clamp(start, summaryBounds.Left, summaryBounds.Right - width);
        return new Rect(start, summaryBounds.Y, width, summaryBounds.Height);
    }

    private void DrawCurrentTimeIndicator(DrawingContext context, DateTimeOffset windowStart, DateTimeOffset windowEnd, double width, double height)
    {
        if (width <= 0)
        {
            return;
        }

        var x = TimeToX(windowEnd, windowStart, windowEnd, width);
        if (double.IsNaN(x) || double.IsInfinity(x))
        {
            return;
        }

        x = Math.Clamp(x, 0, width);
        var startPoint = new Point(x, 0);
        var endPoint = new Point(x, height);
        context.DrawLine(CurrentTimePen, startPoint, endPoint);
    }

    private void DrawHoverMeasurement(DrawingContext context, EventHoverMeasurement measurement)
    {
        var contextInfo = measurement.Context;
        var trackBounds = measurement.TrackBounds;
        var visibleGap = measurement.VisibleGapBounds;
        var pointerX = Math.Clamp(measurement.PointerX, trackBounds.Left, trackBounds.Right);

        var previousEndX = Math.Clamp(TimeToX(measurement.PreviousEventEnd, contextInfo.WindowStart, contextInfo.WindowEnd, contextInfo.Width), trackBounds.Left, trackBounds.Right);
        var nextStartX = Math.Clamp(TimeToX(measurement.NextEventStart, contextInfo.WindowStart, contextInfo.WindowEnd, contextInfo.Width), trackBounds.Left, trackBounds.Right);

        var highlightRect = visibleGap.Width <= 0
            ? new Rect(pointerX, trackBounds.Top, 1, trackBounds.Height)
            : visibleGap;

        context.DrawRectangle(MeasurementHighlightBrush, null, highlightRect);

        context.DrawLine(MeasurementPen, new Point(previousEndX, trackBounds.Top), new Point(previousEndX, trackBounds.Bottom));
        context.DrawLine(MeasurementPen, new Point(nextStartX, trackBounds.Top), new Point(nextStartX, trackBounds.Bottom));
        context.DrawLine(MeasurementPen, new Point(pointerX, trackBounds.Top), new Point(pointerX, trackBounds.Bottom));

        var midY = highlightRect.Top + highlightRect.Height / 2;
        context.DrawLine(MeasurementPen, new Point(previousEndX, midY), new Point(nextStartX, midY));
        context.DrawLine(MeasurementPen, new Point(previousEndX, midY - 4), new Point(previousEndX, midY + 4));
        context.DrawLine(MeasurementPen, new Point(nextStartX, midY - 4), new Point(nextStartX, midY + 4));
        context.DrawLine(MeasurementPen, new Point(pointerX, midY - 4), new Point(pointerX, midY + 4));

        var previousLabel = string.IsNullOrWhiteSpace(measurement.PreviousEvent.Label) ? "Previous event" : measurement.PreviousEvent.Label;
        var nextLabel = string.IsNullOrWhiteSpace(measurement.NextEvent.Label) ? "Next event" : measurement.NextEvent.Label;
        var pointerTimeText = measurement.PointerTime.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
        var gapDuration = measurement.NextEventStart - measurement.PreviousEventEnd;

        static FormattedText CreateText(string text, double size, FontWeight weight = FontWeight.Normal)
        {
            return new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, weight),
                size,
                MeasurementLabelBrush);
        }

        var header = CreateText($"{previousLabel} → {nextLabel}", 12, FontWeight.SemiBold);
        var gapText = CreateText($"Gap {FormatDuration(gapDuration)}", 11);
        var pointerText = CreateText($"Pointer {pointerTimeText}", 11);
        var afterText = CreateText($"{FormatDuration(measurement.AfterPrevious)} after previous", 11);
        var beforeText = CreateText($"{FormatDuration(measurement.BeforeNext)} before next", 11);

        var maxWidth = new[] { header.Width, gapText.Width, pointerText.Width, afterText.Width, beforeText.Width }.Max();
        const double paddingX = 6;
        const double paddingY = 4;
        const double lineSpacing = 2;
        const double verticalMargin = 8;
        const double controlMargin = 2;
        var bubbleWidth = maxWidth + paddingX * 2;
        var bubbleHeight = header.Height + gapText.Height + pointerText.Height + afterText.Height + beforeText.Height + (4 * lineSpacing) + paddingY * 2;

        var gapCenter = visibleGap.Left + visibleGap.Width / 2;
        var bubbleX = gapCenter - bubbleWidth / 2;
        bubbleX = Math.Clamp(bubbleX, trackBounds.Left + controlMargin, trackBounds.Right - bubbleWidth - controlMargin);

        var controlHeight = Bounds.Height;
        var availableAbove = trackBounds.Top;
        var availableBelow = controlHeight - trackBounds.Bottom;
        var placeAbove = availableAbove >= availableBelow;

        var aboveCandidate = trackBounds.Top - bubbleHeight - verticalMargin;
        var belowCandidate = trackBounds.Bottom + verticalMargin;

        var maxAboveY = trackBounds.Top - bubbleHeight - controlMargin;
        var minBelowY = trackBounds.Bottom + controlMargin;
        var maxBelowY = controlHeight - bubbleHeight - controlMargin;

        bool canPlaceAbove = maxAboveY >= controlMargin;
        bool canPlaceBelow = minBelowY <= maxBelowY;

        double bubbleY;
        if ((placeAbove && canPlaceAbove) || (!canPlaceBelow && canPlaceAbove))
        {
            bubbleY = Math.Clamp(aboveCandidate, controlMargin, maxAboveY);
        }
        else if (canPlaceBelow)
        {
            bubbleY = Math.Clamp(belowCandidate, minBelowY, maxBelowY);
        }
        else if (canPlaceAbove)
        {
            bubbleY = Math.Clamp(aboveCandidate, controlMargin, maxAboveY);
        }
        else
        {
            bubbleY = Math.Clamp(belowCandidate, controlMargin, controlHeight - bubbleHeight - controlMargin);
        }

        var bubbleRect = new Rect(bubbleX, bubbleY, bubbleWidth, bubbleHeight);
        context.DrawRectangle(MeasurementBackgroundBrush, MeasurementBorderPen, bubbleRect);

        var textX = bubbleRect.X + paddingX;
        var textY = bubbleRect.Y + paddingY;
        context.DrawText(header, new Point(textX, textY));
        textY += header.Height + lineSpacing;
        context.DrawText(gapText, new Point(textX, textY));
        textY += gapText.Height + lineSpacing;
        context.DrawText(pointerText, new Point(textX, textY));
        textY += pointerText.Height + lineSpacing;
        context.DrawText(afterText, new Point(textX, textY));
        textY += afterText.Height + lineSpacing;
        context.DrawText(beforeText, new Point(textX, textY));
    }

    private static double TimeToX(DateTimeOffset value, DateTimeOffset windowStart, DateTimeOffset windowEnd, double width)
    {
        var total = (windowEnd - windowStart).TotalMilliseconds;
        if (total <= 0)
        {
            return 0;
        }

        var offset = (value - windowStart).TotalMilliseconds;
        return width * offset / total;
    }

    private static double MapTimeToSummary(DateTimeOffset value, (DateTimeOffset Start, DateTimeOffset End) bounds, Rect area)
    {
        var total = (bounds.End - bounds.Start).TotalMilliseconds;
        if (total <= 0 || area.Width <= 0)
        {
            return area.X;
        }

        var offset = (value - bounds.Start).TotalMilliseconds;
        var ratio = offset / total;
        ratio = double.IsNaN(ratio) ? 0 : Math.Clamp(ratio, 0, 1);
        return area.X + ratio * area.Width;
    }

    private DateTimeOffset XToTime(double x, DateTimeOffset windowStart, DateTimeOffset windowEnd, double width)
    {
        var total = (windowEnd - windowStart).TotalMilliseconds;
        if (total <= 0 || width <= 0)
        {
            return windowStart;
        }

        var ratio = x / width;
        ratio = Math.Clamp(ratio, 0, 1);
        var offset = TimeSpan.FromMilliseconds(total * ratio);
        return windowStart + offset;
    }

    private static IEnumerable<TimelineEvent> EnumerateEvents(IEnumerable<TimelineTrack> tracks)
    {
        foreach (var track in tracks)
        {
            foreach (var evt in EnumerateEventTree(track.Events))
            {
                yield return evt;
            }
        }
    }

    private static IEnumerable<TimelineEvent> EnumerateEventTree(IEnumerable<TimelineEvent> events)
    {
        foreach (var evt in events)
        {
            yield return evt;

            foreach (var child in EnumerateEventTree(evt.Children))
            {
                yield return child;
            }
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds >= 1)
        {
            return $"{duration.TotalSeconds:F3} s";
        }

        if (duration.TotalMilliseconds >= 1)
        {
            return $"{duration.TotalMilliseconds:F3} ms";
        }

        return $"{(duration.TotalMilliseconds * 1000):F3} us";
    }

    private static string FormatTimeScalePerDivision(TimeSpan span)
    {
        if (span.TotalSeconds >= 1)
        {
            return span.TotalSeconds >= 10 ? $"{span.TotalSeconds:0} s" : $"{span.TotalSeconds:0.###} s";
        }
        if (span.TotalMilliseconds >= 1)
        {
            return span.TotalMilliseconds >= 10 ? $"{span.TotalMilliseconds:0} ms" : $"{span.TotalMilliseconds:0.###} ms";
        }
        double micro = span.TotalMilliseconds * 1000.0; // microseconds
        if (micro >= 1)
        {
            return micro >= 10 ? $"{micro:0} µs" : $"{micro:0.###} µs";
        }
        double nano = micro * 1000.0; // nanoseconds
        return nano >= 10 ? $"{nano:0} ns" : $"{nano:0.###} ns";
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (Timeline is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        var position = point.Position;
        _lastPointerPosition = position;
        _lastHoverPosition = null;
        ClearHoverMeasurement(true);

        if (_summaryBounds.Contains(position) && _summaryWindowBounds.Width > 0)
        {
            _isSummaryDragging = true;
            var relativeX = position.X - _summaryWindowBounds.X;
            _summaryDragOffsetRatio = _summaryWindowBounds.Width > 0 ? Math.Clamp(relativeX / _summaryWindowBounds.Width, 0, 1) : 0.5;
        }
        else
        {
            _isPanning = true;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var timeline = Timeline;
        if (timeline is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        if (!_isPanning && !_isSummaryDragging)
        {
            _lastPointerPosition = position;
            _lastHoverPosition = position;
            UpdateHoverMeasurement(position, true);
            return;
        }

        var delta = position - _lastPointerPosition;
        _lastPointerPosition = position;
        _lastHoverPosition = null;
        ClearHoverMeasurement(true);

        if (_isPanning && Bounds.Width > 0)
        {
            var ratio = -delta.X / Bounds.Width;
            timeline.PanViewport(ratio);
            e.Handled = true;
        }
        else if (_isSummaryDragging && _summaryBounds.Width > 0)
        {
            var boundsInfo = timeline.GetTimelineBounds();
            var totalMillis = (boundsInfo.End - boundsInfo.Start).TotalMilliseconds;
            if (totalMillis > 0)
            {
                var normalized = (position.X - _summaryBounds.X) / _summaryBounds.Width;
                normalized = Math.Clamp(normalized, 0, 1);
                var target = boundsInfo.Start + TimeSpan.FromMilliseconds(totalMillis * normalized);
                var offset = TimeSpan.FromMilliseconds(timeline.VisibleDuration.TotalMilliseconds * _summaryDragOffsetRatio);
                var newStart = target - offset;
                timeline.MoveViewport(newStart);
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (Equals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
        }

        var position = e.GetPosition(this);
        _lastPointerPosition = position;
        _isPanning = false;
        _isSummaryDragging = false;

        if (Bounds.Contains(position))
        {
            _lastHoverPosition = position;
            UpdateHoverMeasurement(position, true);
        }
        else
        {
            _lastHoverPosition = null;
            ClearHoverMeasurement(true);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _lastHoverPosition = null;
        ClearHoverMeasurement(true);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var timeline = Timeline;
        if (timeline is null || Bounds.Width <= 0)
        {
            return;
        }

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < double.Epsilon)
        {
            return;
        }

        var position = e.GetPosition(this);
        var windowStart = CurrentTime - VisibleDuration;
        var anchor = XToTime(position.X, windowStart, CurrentTime, Bounds.Width);
        var offsetRatio = Bounds.Width > 0 ? position.X / Bounds.Width : 0.5;
        offsetRatio = Math.Clamp(offsetRatio, 0, 1);
        var scale = delta > 0 ? 0.8 : 1.25;
        timeline.ZoomAround(anchor, scale, offsetRatio);
        e.Handled = true;
    }

    private void UpdateHoverMeasurement(Point position, bool requestRender)
    {
        if (_lastRenderContext is not { } context)
        {
            return;
        }

        var measurement = ComputeHoverMeasurement(position, context);

        if (_hoverMeasurement is null && measurement is null)
        {
            return;
        }

        if (_hoverMeasurement is { } existing && measurement is { } updated && existing.Equals(updated))
        {
            return;
        }

        _hoverMeasurement = measurement;
        if (requestRender)
        {
            QueueInvalidateVisual();
        }
    }

    private void ClearHoverMeasurement(bool requestRender)
    {
        if (_hoverMeasurement is null)
        {
            return;
        }

        _hoverMeasurement = null;
        if (requestRender)
        {
            QueueInvalidateVisual();
        }
    }

    private EventHoverMeasurement? ComputeHoverMeasurement(Point position, RenderContext context)
    {
        TimelineTrack? track = null;
        Rect trackBounds = default;

        foreach (var kvp in _trackBounds)
        {
            if (kvp.Value.Contains(position))
            {
                track = kvp.Key;
                trackBounds = kvp.Value;
                break;
            }
        }

        if (track is null)
        {
            return null;
        }

        var rootEvents = track.Events
            .OrderBy(evt => evt.Start)
            .ToList();

        if (rootEvents.Count < 2)
        {
            return null;
        }

        var pointerX = Math.Clamp(position.X, trackBounds.Left, trackBounds.Right);
        var pointerTime = XToTime(pointerX, context.WindowStart, context.WindowEnd, context.Width);

        TimelineEvent? previousEvent = null;
        DateTimeOffset? previousEnd = null;
        TimelineEvent? nextEvent = null;
        DateTimeOffset? nextStart = null;

        foreach (var evt in rootEvents)
        {
            if (evt.End is { } end && end <= pointerTime)
            {
                if (previousEnd is null || end >= previousEnd)
                {
                    previousEvent = evt;
                    previousEnd = end;
                }
            }

            if (nextEvent is null && evt.Start >= pointerTime)
            {
                nextEvent = evt;
                nextStart = evt.Start;
            }

            if (previousEvent is not null && nextEvent is not null && evt.Start > pointerTime)
            {
                break;
            }
        }

        if (previousEvent is null || previousEnd is null || nextEvent is null || nextStart is null)
        {
            return null;
        }

        if (nextStart <= previousEnd)
        {
            return null;
        }

        if (pointerTime < previousEnd || pointerTime > nextStart)
        {
            return null;
        }

        var gapStartX = TimeToX(previousEnd.Value, context.WindowStart, context.WindowEnd, context.Width);
        var gapEndX = TimeToX(nextStart.Value, context.WindowStart, context.WindowEnd, context.Width);

        var visibleStartX = Math.Clamp(gapStartX, trackBounds.Left, trackBounds.Right);
        var visibleEndX = Math.Clamp(gapEndX, trackBounds.Left, trackBounds.Right);

        if (visibleEndX < visibleStartX)
        {
            (visibleStartX, visibleEndX) = (visibleEndX, visibleStartX);
        }

        var visibleWidth = Math.Max(1, visibleEndX - visibleStartX);
        var visibleGap = new Rect(visibleStartX, trackBounds.Top, visibleWidth, trackBounds.Height);

        var afterPrevious = pointerTime - previousEnd.Value;
        var beforeNext = nextStart.Value - pointerTime;

        return new EventHoverMeasurement(
            context,
            track,
            previousEvent,
            nextEvent,
            previousEnd.Value,
            nextStart.Value,
            trackBounds,
            visibleGap,
            pointerX,
            pointerTime,
            afterPrevious,
            beforeNext);
    }

    private void AttachEvent(TimelineEvent timelineEvent)
    {
        timelineEvent.PropertyChanged -= EventOnPropertyChanged;
        timelineEvent.PropertyChanged += EventOnPropertyChanged;

        if (!_eventChildHandlers.ContainsKey(timelineEvent))
        {
            void Handler(object? _, NotifyCollectionChangedEventArgs args)
            {
                if (args.OldItems is not null)
                {
                    foreach (var item in args.OldItems.OfType<TimelineEvent>())
                    {
                        DetachEvent(item);
                    }
                }

                if (args.NewItems is not null)
                {
                    foreach (var item in args.NewItems.OfType<TimelineEvent>())
                    {
                        AttachEvent(item);
                    }
                }

                QueueInvalidateVisual();
            }

            timelineEvent.Children.CollectionChanged += Handler;
            _eventChildHandlers[timelineEvent] = Handler;
        }

        foreach (var child in timelineEvent.Children)
        {
            AttachEvent(child);
        }
    }

    private void DetachEvent(TimelineEvent timelineEvent)
    {
        timelineEvent.PropertyChanged -= EventOnPropertyChanged;

        if (_eventChildHandlers.TryGetValue(timelineEvent, out var handler))
        {
            timelineEvent.Children.CollectionChanged -= handler;
            _eventChildHandlers.Remove(timelineEvent);
        }

        foreach (var child in timelineEvent.Children.ToList())
        {
            DetachEvent(child);
        }
    }

    private sealed class RenderContext
    {
        public RenderContext(DateTimeOffset windowStart, DateTimeOffset windowEnd, double width, double trackAreaHeight)
        {
            WindowStart = windowStart;
            WindowEnd = windowEnd;
            Width = width;
            TrackAreaHeight = trackAreaHeight;
        }

        public DateTimeOffset WindowStart { get; }
        public DateTimeOffset WindowEnd { get; }
        public double Width { get; }
        public double TrackAreaHeight { get; }
    }

    private sealed class EventVisual
    {
        public EventVisual(TimelineTrack track, TimelineEvent timelineEvent, Rect bounds, Rect trackBounds, DateTimeOffset start, DateTimeOffset end, int depth)
        {
            Track = track;
            Event = timelineEvent;
            Bounds = bounds;
            TrackBounds = trackBounds;
            Start = start;
            End = end;
            Depth = depth;
        }

        public TimelineTrack Track { get; }
        public TimelineEvent Event { get; }
        public Rect Bounds { get; }
        public Rect TrackBounds { get; }
        public DateTimeOffset Start { get; }
        public DateTimeOffset End { get; }
        public int Depth { get; }
    }

    private sealed record EventHoverMeasurement(
        RenderContext Context,
        TimelineTrack Track,
        TimelineEvent PreviousEvent,
        TimelineEvent NextEvent,
        DateTimeOffset PreviousEventEnd,
        DateTimeOffset NextEventStart,
        Rect TrackBounds,
        Rect VisibleGapBounds,
        double PointerX,
        DateTimeOffset PointerTime,
        TimeSpan AfterPrevious,
        TimeSpan BeforeNext);

    private static double CalculateGridStep(double totalSeconds, int targetLines)
    {
        var roughStep = totalSeconds / targetLines;
        if (roughStep <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        var residual = roughStep / magnitude;

        return residual switch
        {
            < 2 => magnitude,
            < 5 => 2 * magnitude,
            _ => 5 * magnitude
        };
    }
}
