using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using InstrumentScope.Models;

namespace InstrumentScope.ViewModels;

public partial class TimelineViewModel : ObservableObject, IDisposable
{
    private readonly Dictionary<Guid, TimelineEvent> _eventsById = new();
    private readonly Dictionary<Guid, Guid?> _eventParentById = new();
    private readonly Dictionary<Guid, string> _eventTrackById = new();
    private readonly Dictionary<string, TimelineTrack> _tracksByName = new(StringComparer.OrdinalIgnoreCase);
    private const double HistoryRetentionMultiplier = 4;
    public static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromTicks(10);
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

    public enum TriggerEdge
    {
        Rising,
        Falling,
        Both
    }

    private bool _triggerEnabled;
    private string? _triggerTrackName;
    private string? _triggerEventLabel;
    private TriggerEdge _triggerEdge = TriggerEdge.Rising;
    private double _triggerOffsetWithinWindow = 0.2; // 20% from left by default
    private DateTimeOffset? _lastTriggerTime;

    public bool TriggerEnabled
    {
        get => _triggerEnabled;
        set
        {
            if (SetProperty(ref _triggerEnabled, value))
            {
                // In trigger mode, do not scroll automatically
                if (value)
                {
                    PauseLive();
                }
            }
        }
    }

    public string? TriggerTrackName
    {
        get => _triggerTrackName;
        set
        {
            if (SetProperty(ref _triggerTrackName, value))
            {
                OnPropertyChanged(nameof(AvailableEventLabels));
            }
        }
    }

    public string? TriggerEventLabel
    {
        get => _triggerEventLabel;
        set => SetProperty(ref _triggerEventLabel, value);
    }

    public TriggerEdge TriggerMode
    {
        get => _triggerEdge;
        set => SetProperty(ref _triggerEdge, value);
    }

    /// <summary>
    /// The horizontal position of the trigger line within the viewport (0..1). Default 0.2 (20%).
    /// </summary>
    public double TriggerOffsetWithinWindow
    {
        get => _triggerOffsetWithinWindow;
        set
        {
            var clamped = double.IsNaN(value) ? 0.2 : Math.Clamp(value, 0.0, 1.0);
            SetProperty(ref _triggerOffsetWithinWindow, clamped);
        }
    }

    public DateTimeOffset? LastTriggerTime
    {
        get => _lastTriggerTime;
        private set => SetProperty(ref _lastTriggerTime, value);
    }

    public bool IsTriggerArmed => TriggerEnabled && !string.IsNullOrWhiteSpace(TriggerTrackName) && !string.IsNullOrWhiteSpace(TriggerEventLabel);

    private static readonly Regex TrailingDurationRegex = new(
        pattern: @"\s+(?<num>\d+(?:[\.,]\d+)?)\s*(?<unit>ns|us|µs|μs|ms|s|sec|secs|seconds|m|min|minutes|h|hr|hours)$",
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string NormalizeLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return label;
        var trimmed = label.Trim();
        var match = TrailingDurationRegex.Match(trimmed);
        if (match.Success)
        {
            // Remove the matched trailing " numeric+unit" part
            var without = trimmed[..match.Index];
            return without.TrimEnd();
        }
        return trimmed;
    }

    public IEnumerable<string> AvailableTrackNames => Tracks.Select(t => t.Name);

    public IEnumerable<string> AvailableEventLabels
    {
        get
        {
            if (string.IsNullOrWhiteSpace(TriggerTrackName)) return Enumerable.Empty<string>();
            if (!_tracksByName.TryGetValue(TriggerTrackName, out var track)) return Enumerable.Empty<string>();
            return EnumerateEventTree(track.Events)
                .Select(e => NormalizeLabel(e.Label))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal);
        }
    }

    public Array TriggerModes => Enum.GetValues(typeof(TriggerEdge));

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

    public TimelineTrack EnsureTrack(string name)
    {
        if (_tracksByName.TryGetValue(name, out var track))
        {
            return track;
        }

        track = new TimelineTrack(name);
        _tracksByName[name] = track;
        Tracks.Add(track);
        OnPropertyChanged(nameof(AvailableTrackNames));
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

        OnPropertyChanged(nameof(AvailableEventLabels));

        // Trigger on rising edge (event start)
        OnTriggerCandidate(trackName, label, timestamp, rising: true);
    }

    public void ApplyEventStop(Guid eventId, DateTimeOffset timestamp)
    {
        if (_eventsById.TryGetValue(eventId, out var timelineEvent))
        {
            timelineEvent.Complete(timestamp);
            OnPropertyChanged(nameof(AvailableEventLabels));
            // Determine track and label from stored maps
            if (_eventTrackById.TryGetValue(eventId, out var trackName))
            {
                OnTriggerCandidate(trackName, timelineEvent.Label, timestamp, rising: false);
            }
        }
    }

    public void ApplyCompleteEvent(Guid eventId, string trackName, DateTimeOffset startTimestamp, DateTimeOffset endTimestamp, string label, Color color, Guid? parentId = null)
    {
        if (_eventsById.TryGetValue(eventId, out var existing))
        {
            RemoveEventFromContainers(existing);
        }

        var track = EnsureTrack(trackName);
        var timelineEvent = new TimelineEvent(eventId, startTimestamp, label, color, parentId);
        timelineEvent.Complete(endTimestamp);

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

        OnPropertyChanged(nameof(AvailableEventLabels));

        // Consider both edges for a complete event
        OnTriggerCandidate(trackName, label, startTimestamp, rising: true);
        OnTriggerCandidate(trackName, label, endTimestamp, rising: false);
    }

    private void OnTriggerCandidate(string trackName, string label, DateTimeOffset timestamp, bool rising)
    {
        if (!IsTriggerArmed)
        {
            return;
        }

        // Edge filter
        if (TriggerMode == TriggerEdge.Rising && !rising) return;
        if (TriggerMode == TriggerEdge.Falling && rising) return;

        // Track and label match
        if (!string.Equals(trackName, TriggerTrackName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var normalized = NormalizeLabel(label);
        if (!string.Equals(normalized, TriggerEventLabel, StringComparison.Ordinal))
        {
            return;
        }

        AnchorToTrigger(timestamp);
        LastTriggerTime = timestamp;
    }

    private void AnchorToTrigger(DateTimeOffset timestamp)
    {
        // Freeze live scrolling while triggered
        IsLive = false;

        var offset = TimeSpan.FromMilliseconds(VisibleDuration.TotalMilliseconds * TriggerOffsetWithinWindow);
        var proposedStart = timestamp - offset;
        var proposedEnd = proposedStart + VisibleDuration;

        var bounds = GetTimelineBoundsCore();
        var clamped = ClampWindowToBounds(proposedStart, proposedEnd, bounds);
        ViewportEnd = clamped.End;
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
            if (TriggerEnabled && LastTriggerTime is { } t)
            {
                AnchorToTrigger(t);
                return;
            }
            GoLive();
            return;
        }

        if (TriggerEnabled && LastTriggerTime is { } last)
        {
            AnchorToTrigger(last);
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
        if (TriggerEnabled && LastTriggerTime is { } t)
        {
            AnchorToTrigger(t);
        }
        else
        {
            GoLive();
        }
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
        // When panning manually in trigger mode, keep the trigger line fixed (no special handling needed).
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
