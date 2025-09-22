using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Diagnostics.Models;

namespace Diagnostics.ViewModels;

public partial class TimelineViewModel : ObservableObject, IDisposable
{
    private readonly Dictionary<Guid, TimelineEvent> _eventsById = new();
    private readonly Dictionary<Guid, Guid?> _eventParentById = new();
    private readonly Dictionary<Guid, string> _eventTrackById = new();
    private readonly Dictionary<string, TimelineTrack> _tracksByName = new(StringComparer.OrdinalIgnoreCase);
    private const double HistoryRetentionMultiplier = 4;
    public static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromMilliseconds(5);
    public static readonly TimeSpan MaximumVisibleDuration = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _timer;
    private readonly TimeSpan _defaultVisibleDuration = TimeSpan.FromSeconds(30);

    private DateTimeOffset _currentTime;
    private DateTimeOffset _viewportEnd;
    private TimeSpan _visibleDuration;
    private bool _isCapturing;
    private bool _isLive = true;
    private DateTimeOffset? _captureStartTime;
    private DateTimeOffset? _captureEndTime;

    public TimelineViewModel()
    {
        _currentTime = DateTimeOffset.UtcNow;
        _viewportEnd = _currentTime;
        _visibleDuration = _defaultVisibleDuration;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, OnTick);
        _timer.Start();
    }

    public ObservableCollection<TimelineTrack> Tracks { get; } = new();

    public TimeSpan VisibleDuration
    {
        get => _visibleDuration;
        set
        {
            var clamped = ClampVisibleDuration(value);
            if (SetProperty(ref _visibleDuration, clamped))
            {
                OnPropertyChanged(nameof(RetainedDuration));
            }
        }
    }

    public TimeSpan RetainedDuration => TimeSpan.FromTicks((long)(VisibleDuration.Ticks * HistoryRetentionMultiplier));

    public DateTimeOffset CurrentTime
    {
        get => _currentTime;
        private set => SetProperty(ref _currentTime, value);
    }

    public DateTimeOffset ViewportEnd
    {
        get => _viewportEnd;
        private set => SetProperty(ref _viewportEnd, value);
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set => SetProperty(ref _isCapturing, value);
    }

    public bool IsLive
    {
        get => _isLive;
        private set => SetProperty(ref _isLive, value);
    }

    public DateTimeOffset? CaptureStartTime
    {
        get => _captureStartTime;
        private set => SetProperty(ref _captureStartTime, value);
    }

    public DateTimeOffset? CaptureEndTime
    {
        get => _captureEndTime;
        private set => SetProperty(ref _captureEndTime, value);
    }

    public TimelineTrack EnsureTrack(string name)
    {
        if (_tracksByName.TryGetValue(name, out var track))
        {
            return track;
        }

        track = new TimelineTrack(name);
        _tracksByName[name] = track;
        Tracks.Add(track);
        return track;
    }

    public void ApplyEventStart(Guid eventId, string trackName, DateTimeOffset timestamp, string label, Color color, Guid? parentId = null)
    {
        if (_eventsById.TryGetValue(eventId, out var existing))
        {
            RemoveEventFromContainers(existing);
        }

        var track = EnsureTrack(trackName);
        var timelineEvent = new TimelineEvent(eventId, timestamp, label, color, parentId);
        _eventsById[eventId] = timelineEvent;
        _eventParentById[eventId] = parentId;
        _eventTrackById[eventId] = trackName;

        if (parentId is { } parentGuid && _eventsById.TryGetValue(parentGuid, out var parentEvent))
        {
            InsertEventOrdered(parentEvent.Children, timelineEvent);
        }
        else
        {
            InsertEventOrdered(track.Events, timelineEvent);
        }

    }

    public void ApplyEventStop(Guid eventId, DateTimeOffset timestamp)
    {
        if (_eventsById.TryGetValue(eventId, out var timelineEvent))
        {
            timelineEvent.Complete(timestamp);
        }
    }

    private void RemoveEventFromContainers(TimelineEvent timelineEvent)
    {
        if (_eventParentById.TryGetValue(timelineEvent.Id, out var parentId) && parentId is { } parentGuid &&
            _eventsById.TryGetValue(parentGuid, out var parentEvent))
        {
            parentEvent.Children.Remove(timelineEvent);
        }
        else if (_eventTrackById.TryGetValue(timelineEvent.Id, out var trackName) &&
                 _tracksByName.TryGetValue(trackName, out var track))
        {
            track.Events.Remove(timelineEvent);
        }

        foreach (var child in timelineEvent.Children.ToList())
        {
            RemoveEvent(child);
        }
    }

    private void RemoveEvent(TimelineEvent timelineEvent)
    {
        RemoveEventFromContainers(timelineEvent);
        _eventsById.Remove(timelineEvent.Id);
        _eventParentById.Remove(timelineEvent.Id);
        _eventTrackById.Remove(timelineEvent.Id);
    }

    private static void InsertEventOrdered(IList<TimelineEvent> collection, TimelineEvent timelineEvent)
    {
        if (collection.Count == 0)
        {
            collection.Add(timelineEvent);
            return;
        }

        var insertIndex = collection.Count;
        for (var index = 0; index < collection.Count; index++)
        {
            if (timelineEvent.Start < collection[index].Start)
            {
                insertIndex = index;
                break;
            }
        }

        if (insertIndex == collection.Count)
        {
            collection.Add(timelineEvent);
        }
        else
        {
            collection.Insert(insertIndex, timelineEvent);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }

    public void StartCapture()
    {
        CaptureStartTime = DateTimeOffset.UtcNow;
        CaptureEndTime = null;
        IsCapturing = true;
    }

    public void StopCapture()
    {
        if (!IsCapturing)
        {
            return;
        }

        IsCapturing = false;
        CaptureEndTime = GetLatestTimestamp();

        if (CaptureEndTime is { } end)
        {
            ViewportEnd = end;
        }

        PauseLive();
    }

    public void ClearTimeline()
    {
        ClearAllEvents();
        IsCapturing = false;
        CaptureStartTime = null;
        CaptureEndTime = null;
        GoLive();
    }

    public void GoLive()
    {
        var now = DateTimeOffset.UtcNow;
        CurrentTime = now;
        ViewportEnd = now;
        IsLive = true;
    }

    public void PauseLive()
    {
        IsLive = false;
    }

    public void SetPlaybackTime(DateTimeOffset timestamp)
    {
        PauseLive();
        ViewportEnd = timestamp;
    }

    public void AdjustZoom(double scale)
    {
        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var targetTicks = Math.Max(1L, (long)Math.Round(VisibleDuration.Ticks * scale, MidpointRounding.AwayFromZero));
        var newDuration = TimeSpan.FromTicks(targetTicks);
        newDuration = ClampVisibleDuration(newDuration);

        var restoreLive = newDuration >= _defaultVisibleDuration;

        VisibleDuration = newDuration;

        if (restoreLive)
        {
            GoLive();
            return;
        }

        IsLive = false;

        var bounds = GetTimelineBoundsCore();
        var windowStart = ViewportEnd - VisibleDuration;
        var clamped = ClampWindowToBounds(windowStart, ViewportEnd, bounds);
        ViewportEnd = clamped.End;
    }

    public void ResetZoom()
    {
        VisibleDuration = _defaultVisibleDuration;
        GoLive();
    }

    public void PanViewport(double ratio)
    {
        if (Math.Abs(ratio) < double.Epsilon)
        {
            return;
        }

        var deltaTicks = (long)Math.Round(VisibleDuration.Ticks * ratio, MidpointRounding.AwayFromZero);
        if (deltaTicks == 0)
        {
            deltaTicks = ratio > 0 ? 1 : -1;
        }

        Pan(TimeSpan.FromTicks(deltaTicks));
    }

    public void Pan(TimeSpan delta)
    {
        if (delta == TimeSpan.Zero)
        {
            return;
        }

        IsLive = false;

        var bounds = GetTimelineBoundsCore();
        var maxEnd = bounds.End;
        var minEnd = bounds.Start + VisibleDuration;
        if (minEnd > maxEnd)
        {
            minEnd = maxEnd;
        }

        var candidate = ViewportEnd + delta;
        candidate = Clamp(candidate, minEnd, maxEnd);
        ViewportEnd = candidate;
    }

    public void ZoomAround(DateTimeOffset anchor, double scale, double offsetWithinWindow = 0.5)
    {
        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var oldDuration = VisibleDuration;
        var newDurationTicks = (long)Math.Round(oldDuration.Ticks * scale, MidpointRounding.AwayFromZero);
        newDurationTicks = Math.Max(1L, newDurationTicks);
        var newDuration = TimeSpan.FromTicks(newDurationTicks);
        newDuration = ClampVisibleDuration(newDuration);

        var restoreLive = newDuration >= _defaultVisibleDuration;

        var bounds = GetTimelineBoundsCore();
        anchor = Clamp(anchor, bounds.Start, bounds.End);
        offsetWithinWindow = double.IsNaN(offsetWithinWindow) ? 0.5 : Math.Clamp(offsetWithinWindow, 0, 1);

        VisibleDuration = newDuration;

        if (restoreLive)
        {
            GoLive();
            return;
        }

        IsLive = false;

        var newStart = anchor - TimeSpan.FromMilliseconds(newDuration.TotalMilliseconds * offsetWithinWindow);
        var newEnd = newStart + newDuration;

        var clamped = ClampWindowToBounds(newStart, newEnd, bounds);
        ViewportEnd = clamped.End;
    }

    public (DateTimeOffset Start, DateTimeOffset End) GetTimelineBounds()
    {
        return GetTimelineBoundsCore();
    }

    public void MoveViewport(DateTimeOffset windowStart)
    {
        IsLive = false;
        var bounds = GetTimelineBoundsCore();
        var clamped = ClampWindowToBounds(windowStart, windowStart + VisibleDuration, bounds);
        ViewportEnd = clamped.End;
    }

    private DateTimeOffset GetLatestTimestamp()
    {
        var latestStart = _eventsById.Count == 0 ? (DateTimeOffset?)null : _eventsById.Values.Max(e => e.Start);
        var latestEnd = _eventsById.Values
            .Where(e => e.End.HasValue)
            .Select(e => e.End!.Value)
            .DefaultIfEmpty(latestStart ?? CurrentTime)
            .Max();

        if (IsCapturing && IsLive)
        {
            latestEnd = DateTimeOffset.UtcNow;
        }

        if (CaptureEndTime is { } captureEnd && captureEnd > latestEnd)
        {
            latestEnd = captureEnd;
        }

        var now = DateTimeOffset.UtcNow;
        if (latestEnd < now)
        {
            latestEnd = now;
        }

        return latestEnd;
    }

    private DateTimeOffset GetEarliestTimestamp()
    {
        DateTimeOffset? earliest = null;

        if (CaptureStartTime is { } captureStart)
        {
            earliest = captureStart;
        }

        if (_eventsById.Count > 0)
        {
            var firstEvent = _eventsById.Values.Min(e => e.Start);
            if (earliest is null)
            {
                earliest = firstEvent;
            }
            else if (CaptureStartTime is null && firstEvent < earliest.Value)
            {
                earliest = firstEvent;
            }
        }

        return earliest ?? ViewportEnd - VisibleDuration;
    }

    private (DateTimeOffset Start, DateTimeOffset End) GetTimelineBoundsCore()
    {
        var earliest = GetEarliestTimestamp();
        var latest = GetLatestTimestamp();

        if (latest < earliest)
        {
            latest = earliest + VisibleDuration;
        }

        return (earliest, latest);
    }

    private (DateTimeOffset Start, DateTimeOffset End) ClampWindowToBounds(DateTimeOffset proposedStart, DateTimeOffset proposedEnd, (DateTimeOffset Start, DateTimeOffset End) bounds)
    {
        var duration = proposedEnd - proposedStart;
        if (duration <= TimeSpan.Zero)
        {
            duration = VisibleDuration;
        }

        var start = proposedStart;
        var end = proposedEnd;

        if (start < bounds.Start)
        {
            var delta = bounds.Start - start;
            start += delta;
            end += delta;
        }

        if (end > bounds.End)
        {
            var delta = end - bounds.End;
            start -= delta;
            end -= delta;
        }

        var minEnd = bounds.Start + duration;
        if (minEnd > bounds.End)
        {
            start = bounds.Start;
            end = bounds.End;
        }

        if (end - start != duration)
        {
            end = start + duration;
        }

        return (start, end);
    }

    private static DateTimeOffset Clamp(DateTimeOffset value, DateTimeOffset min, DateTimeOffset max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private void ClearAllEvents()
    {
        foreach (var track in Tracks)
        {
            track.Events.Clear();
        }

        Tracks.Clear();
        _tracksByName.Clear();
        _eventsById.Clear();
        _eventParentById.Clear();
        _eventTrackById.Clear();
    }

    private static TimeSpan ClampVisibleDuration(TimeSpan duration)
    {
        if (duration < MinimumVisibleDuration)
        {
            return MinimumVisibleDuration;
        }

        if (duration > MaximumVisibleDuration)
        {
            return MaximumVisibleDuration;
        }

        return duration;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.UtcNow;
        CurrentTime = now;
        if (IsLive)
        {
            ViewportEnd = now;
        }
    }
}
