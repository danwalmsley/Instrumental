using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Diagnostics.Models;

namespace Diagnostics.Controls;

public class RealtimeTimelineControl : Control
{
    private readonly Dictionary<TimelineTrack, NotifyCollectionChangedEventHandler?> _trackHandlers = new();
    private readonly Dictionary<TimelineEvent, NotifyCollectionChangedEventHandler> _eventChildHandlers = new();
    private static readonly ImmutableSolidColorBrush TrackBackgroundBrush = new(Color.FromArgb(24, 255, 255, 255));
    private static readonly Pen TrackSeparatorPen = new(new ImmutableSolidColorBrush(Color.FromArgb(32, 255, 255, 255)), 1);
    private static readonly Pen GridPen = new(new ImmutableSolidColorBrush(Color.FromArgb(48, 255, 255, 255)), 1);
    private static readonly Pen CurrentTimePen = new(new ImmutableSolidColorBrush(Color.FromArgb(192, 255, 140, 0)), 2);

    public static readonly StyledProperty<IList<TimelineTrack>?> TracksProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, IList<TimelineTrack>?>(nameof(Tracks));

    public static readonly StyledProperty<DateTimeOffset> CurrentTimeProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, DateTimeOffset>(nameof(CurrentTime), DateTimeOffset.UtcNow);

    public static readonly StyledProperty<TimeSpan> VisibleDurationProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, TimeSpan>(nameof(VisibleDuration), TimeSpan.FromSeconds(30));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<RealtimeTimelineControl, IBrush?>(nameof(Background));

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
                InvalidateVisual();
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

        InvalidateVisual();
    }

    private void AttachTrack(TimelineTrack track)
    {
        if (_trackHandlers.ContainsKey(track))
        {
            return;
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

            InvalidateVisual();
        }

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

        _trackHandlers.Remove(track);
    }

    private void EventOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        InvalidateVisual();
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

        DrawTimeGrid(context, windowStart, now, visibleDuration, width, height);

        var tracks = Tracks;
        var trackCount = tracks?.Count ?? 0;
        if (trackCount > 0 && tracks is not null)
        {
            var trackHeight = height / trackCount;
            for (var index = 0; index < trackCount; index++)
            {
                var track = tracks[index];
                var top = index * trackHeight;
                DrawTrack(context, track, windowStart, now, visibleDuration, width, trackHeight, top);
            }
        }

        DrawCurrentTimeIndicator(context, windowStart, now, width, height);
    }

    private void DrawTimeGrid(DrawingContext context, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeSpan visibleDuration, double width, double height)
    {
        var totalSeconds = visibleDuration.TotalSeconds;
        if (totalSeconds <= 0)
        {
            return;
        }

        var targetLines = (int)Math.Clamp(totalSeconds, 10, 60);
        var step = CalculateGridStep(totalSeconds, targetLines);
        var pen = GridPen;
        var firstTickSeconds = Math.Ceiling(windowStart.ToUnixTimeMilliseconds() / (step * 1000)) * step;
        for (var tickSeconds = firstTickSeconds; tickSeconds <= windowEnd.ToUnixTimeMilliseconds() / 1000.0; tickSeconds += step)
        {
            var tickTime = DateTimeOffset.FromUnixTimeMilliseconds((long)(tickSeconds * 1000));
            var x = TimeToX(tickTime, windowStart, windowEnd, width);
            if (x < 0 || x > width)
            {
                continue;
            }

            context.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }
    }

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

    private void DrawTrack(DrawingContext context, TimelineTrack track, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeSpan visibleDuration, double width, double trackHeight, double top)
    {
        var trackBounds = new Rect(0, top, width, trackHeight);
        context.DrawRectangle(TrackBackgroundBrush, null, trackBounds);
        context.DrawLine(TrackSeparatorPen, new Point(trackBounds.Left, trackBounds.Bottom), new Point(trackBounds.Right, trackBounds.Bottom));

        if (track.Events.Count == 0)
        {
            return;
        }

        foreach (var timelineEvent in track.Events)
        {
            DrawEvent(context, timelineEvent, windowStart, windowEnd, width, trackBounds, depth: 0);
        }
    }

    private void DrawEvent(DrawingContext context, TimelineEvent timelineEvent, DateTimeOffset windowStart, DateTimeOffset windowEnd, double width, Rect availableBounds, int depth)
    {
        var eventEnd = timelineEvent.End ?? windowEnd;
        if (eventEnd <= windowStart || timelineEvent.Start >= windowEnd)
        {
            return;
        }

        var startX = TimeToX(timelineEvent.Start, windowStart, windowEnd, width);
        var endX = TimeToX(eventEnd, windowStart, windowEnd, width);
        if (endX <= startX)
        {
            endX = startX + 1;
        }

        startX = Math.Clamp(startX, availableBounds.Left, availableBounds.Right);
        endX = Math.Clamp(endX, availableBounds.Left, availableBounds.Right);

        var availableWidth = endX - startX;
        if (availableWidth <= 0)
        {
            return;
        }

        const double margin = 2;
        var scale = depth == 0 ? 1 : Math.Pow(0.75, depth);
        var eventHeight = availableBounds.Height * (depth == 0 ? 0.9 : 0.6 * scale);
        var verticalOffset = availableBounds.Top + (availableBounds.Height - eventHeight) / 2;
        var rect = new Rect(startX + margin, verticalOffset + margin, availableWidth - margin * 2, eventHeight - margin * 2);

        var roundedRect = new RoundedRect(rect, timelineEvent.CornerRadius);
        context.DrawRectangle(timelineEvent.Fill, null, roundedRect);

        if (!string.IsNullOrWhiteSpace(timelineEvent.Label) && rect.Width > 20)
        {
            var typeface = new Typeface("Segoe UI");
            var formattedText = new FormattedText(
                timelineEvent.Label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                12,
                Brushes.White);

            formattedText.MaxTextWidth = Math.Max(0, rect.Width - 8);
            formattedText.MaxTextHeight = Math.Max(0, rect.Height - 8);
            formattedText.TextAlignment = TextAlignment.Left;
            formattedText.Trimming = TextTrimming.CharacterEllipsis;

            var textOrigin = new Point(rect.X + 4, rect.Y + 4);
            context.DrawText(formattedText, textOrigin);
        }

        foreach (var child in timelineEvent.Children)
        {
            DrawEvent(context, child, windowStart, windowEnd, width, rect, depth + 1);
        }
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

                InvalidateVisual();
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
}
