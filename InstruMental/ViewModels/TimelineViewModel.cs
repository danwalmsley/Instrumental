using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using InstruMental.Models;

namespace InstruMental.ViewModels;

public class TimelineViewModel : ObservableObject, IDisposable
{
    private readonly Dictionary<Guid, TimelineEvent> _eventsById = new();
    private readonly Dictionary<Guid, Guid?> _eventParentById = new();
    private readonly Dictionary<Guid, string> _eventTrackById = new();
    private readonly Dictionary<string, TimelineTrack> _tracksByName = new(StringComparer.OrdinalIgnoreCase);
    private const double HistoryRetentionMultiplier = 4;
    // Number of major divisions across the visible width (matches RealtimeTimelineControl)
    public const int OscilloscopeMajorDivisions = 10;
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
                // VisibleDuration affects the mapping between fraction and time offset; update derived properties
                // Recompute fraction to keep same time offset mapping after zoom change
                if (VisibleDuration.Ticks > 0)
                {
                    var half = TimeSpan.FromTicks(VisibleDuration.Ticks / 2);
                    var ratio = half.Ticks > 0 ? (double)TriggerMeasurePosition.Ticks / half.Ticks : 0.0; // -1..1
                    var frac = (ratio + 1.0) / 2.0; // 0..1
                    if (!double.IsFinite(frac)) frac = 0.5;
                    _triggerMeasurePositionFraction = Math.Clamp(frac, 0.0, 1.0);
                }
                OnPropertyChanged(nameof(TriggerMeasurePositionFraction));
                OnPropertyChanged(nameof(TriggerMeasurePositionDivText));
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
    private DateTimeOffset? _lastTriggerTime;
    private DateTimeOffset? _activeTriggerTime;

    private TimeSpan _triggerHoldoff = TimeSpan.Zero; // 0 disables holdoff
    private string _triggerHoldoffText = "0 ms";
    public string TriggerHoldoffText
    {
        get => _triggerHoldoffText;
        set
        {
            var text = value.Trim();
            if (TryParseDuration(text, out var span))
            {
                TriggerHoldoff = span;
                var normalized = FormatDurationShort(TriggerHoldoff);
                SetProperty(ref _triggerHoldoffText, normalized);
            }
            else
            {
                var formatted = FormatDurationShort(TriggerHoldoff);
                SetProperty(ref _triggerHoldoffText, formatted);
            }
        }
    }
    public TimeSpan TriggerHoldoff
    {
        get => _triggerHoldoff;
        set
        {
            if (SetProperty(ref _triggerHoldoff, value))
            {
                var formatted = FormatDurationShort(value);
                if (!string.Equals(_triggerHoldoffText, formatted, StringComparison.Ordinal))
                {
                    _triggerHoldoffText = formatted;
                    OnPropertyChanged(nameof(TriggerHoldoffText));
                }
            }
        }
    }
    private TimeSpan _triggerMeasurePosition = TimeSpan.Zero; // 0 = center
    private string _triggerMeasurePositionText = "0 µs";
    // Fractional slider position: 0 = leftmost, 1 = rightmost. Maps to TriggerMeasurePosition (time offset from center).
    private double _triggerMeasurePositionFraction = 0.5; // center
    public double TriggerMeasurePositionFraction
    {
        get => _triggerMeasurePositionFraction;
        set
        {
            // clamp to [0,1]
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (SetProperty(ref _triggerMeasurePositionFraction, clamped))
            {
                // Map fraction to time offset relative to center. 0->left(-half), 0.5->center(0), 1->right(+half)
                var half = TimeSpan.FromTicks(VisibleDuration.Ticks / 2);
                var ratio = clamped * 2.0 - 1.0; // -1..1
                var ticks = (long)(ratio * half.Ticks);
                TriggerMeasurePosition = TimeSpan.FromTicks(ticks);
                OnPropertyChanged(nameof(TriggerMeasurePositionDivText));
            }
        }
    }

    // Human readable division-based text e.g. "-2.50 div (-200 ms)" which takes current zoom (VisibleDuration) into account.
    public string TriggerMeasurePositionDivText
    {
        get
        {
            var perDiv = TimeSpan.FromTicks(VisibleDuration.Ticks / OscilloscopeMajorDivisions);
            // divisions offset relative to center (signed)
            double divisions = perDiv.Ticks > 0 ? (double)TriggerMeasurePosition.Ticks / perDiv.Ticks : 0.0;
            var divisionsStr = divisions.ToString("0.##", CultureInfo.CurrentCulture);
            var timeStr = FormatDurationShort(TriggerMeasurePosition);
            return $"{(divisions >= 0 ? "+" : "")}{divisionsStr} div ({timeStr})";
        }
    }

    public bool TriggerEnabled
    {
        get => _triggerEnabled;
        set
        {
            if (SetProperty(ref _triggerEnabled, value))
            {
                OnPropertyChanged(nameof(IsTriggerArmed));
                if (value)
                {
                    // In trigger mode, do not scroll automatically and anchor to last if available
                    PauseLive();
                    if (ActiveTriggerTime is { } t)
                    {
                        AnchorToTrigger(t);
                    }
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
                OnPropertyChanged(nameof(IsTriggerArmed));
            }
        }
    }

    public string? TriggerEventLabel
    {
        get => _triggerEventLabel;
        set
        {
            if (SetProperty(ref _triggerEventLabel, value))
            {
                OnPropertyChanged(nameof(IsTriggerArmed));
            }
        }
    }

    public TriggerEdge TriggerMode
    {
        get => _triggerEdge;
        set => SetProperty(ref _triggerEdge, value);
    }

    public bool IsTriggerArmed => TriggerEnabled && !string.IsNullOrWhiteSpace(TriggerTrackName) && !string.IsNullOrWhiteSpace(TriggerEventLabel);

    public DateTimeOffset? LastTriggerTime
    {
        get => _lastTriggerTime;
        private set => SetProperty(ref _lastTriggerTime, value);
    }

    public DateTimeOffset? ActiveTriggerTime
    {
        get => _activeTriggerTime;
        private set => SetProperty(ref _activeTriggerTime, value);
    }

    // Measure position: time-based horizontal offset from center
    public TimeSpan TriggerMeasurePosition
    {
        get => _triggerMeasurePosition;
        set
        {
            if (SetProperty(ref _triggerMeasurePosition, value))
            {
                var formatted = FormatDurationShort(value);
                if (!string.Equals(_triggerMeasurePositionText, formatted, StringComparison.Ordinal))
                {
                    _triggerMeasurePositionText = formatted;
                    OnPropertyChanged(nameof(TriggerMeasurePositionText));
                }
                // Update fraction representation whenever the underlying time offset changes (or when zoom changes)
                // Guard against division by zero
                if (VisibleDuration.Ticks > 0)
                {
                    var half = TimeSpan.FromTicks(VisibleDuration.Ticks / 2);
                    var ratio = half.Ticks > 0 ? (double)value.Ticks / half.Ticks : 0.0; // -1..1
                    var frac = (ratio + 1.0) / 2.0; // 0..1
                    if (!double.IsFinite(frac)) frac = 0.5;
                    _triggerMeasurePositionFraction = Math.Clamp(frac, 0.0, 1.0);
                    OnPropertyChanged(nameof(TriggerMeasurePositionFraction));
                }
                // Notify derived division-based text
                OnPropertyChanged(nameof(TriggerMeasurePositionDivText));
                if (TriggerEnabled && LastTriggerTime is { } t)
                {
                    AnchorToTrigger(t);
                }
            }
        }
    }

    public string TriggerMeasurePositionText
    {
        get => _triggerMeasurePositionText;
        set
        {
            var text = value.Trim();
            if (TryParseSignedDuration(text, out var span))
            {
                TriggerMeasurePosition = span;
                var normalized = FormatDurationShort(TriggerMeasurePosition);
                SetProperty(ref _triggerMeasurePositionText, normalized);
            }
            else
            {
                var formatted = FormatDurationShort(TriggerMeasurePosition);
                SetProperty(ref _triggerMeasurePositionText, formatted);
            }
        }
    }

    private static readonly Regex TrailingDurationRegex = new(
        pattern: @"\s+(?<num>\d+(?:[\.,]\d+)?)\s*(?<unit>ns|us|µs|μs|ms|s|sec|secs|seconds|m|min|minutes|h|hr|hours)$",
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DurationParseRegex = new(
        pattern: @"^\s*(?<num>\d+(?:[\.,]\d+)?)\s*(?<unit>ns|us|µs|μs|ms|s)?\s*$",
        options: RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SignedDurationParseRegex = new(
        pattern: @"^\s*(?<sign>[+-])?\s*(?<num>\d+(?:[\.,]\d+)?)\s*(?<unit>ns|us|µs|μs|ms|s)?\s*$",
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

        // Holdoff: ignore triggers that occur before the holdoff window elapses
        if (LastTriggerTime is { } last && TriggerHoldoff > TimeSpan.Zero)
        {
            if (timestamp <= last + TriggerHoldoff)
            {
                return; // still in holdoff window
            }
        }

        AnchorToTrigger(timestamp);
        ActiveTriggerTime = timestamp;
        LastTriggerTime = timestamp;
    }

    private void AnchorToTrigger(DateTimeOffset timestamp)
    {
        // Freeze live scrolling while triggered
        IsLive = false;

        var vis = VisibleDuration;
        if (vis <= TimeSpan.Zero)
        {
            vis = TimeSpan.FromMilliseconds(1);
        }

        // Place timestamp at center + measure offset
        var measure = TriggerMeasurePosition;
        // clamp measure to half window to keep it within the viewport after clamping
        var half = TimeSpan.FromTicks(vis.Ticks / 2);
        if (measure > half) measure = half;
        if (measure < -half) measure = -half;

        var proposedStart = timestamp - measure - half;
        var proposedEnd = proposedStart + vis;

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
        ActiveTriggerTime = null;
        LastTriggerTime = null;
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
            if (TriggerEnabled && ActiveTriggerTime is { } t)
            {
                AnchorToTrigger(t);
                return;
            }
            GoLive();
            return;
        }

        if (TriggerEnabled && ActiveTriggerTime is { } last)
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
        if (TriggerEnabled && ActiveTriggerTime is { } t)
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

        if (restoreLive && !TriggerEnabled)
        {
            GoLive();
            return;
        }

        // Keep trigger anchored if armed and we have an active trigger time; do not update to a newer one during zoom
        if (TriggerEnabled && ActiveTriggerTime is { } t)
        {
            AnchorToTrigger(t);
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

    private static bool TryParseDuration(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true; // empty -> 0
        }
        var m = DurationParseRegex.Match(text);
        if (!m.Success)
        {
            return false;
        }
        var numStr = m.Groups["num"].Value.Replace(',', '.');
        if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        var unit = m.Groups["unit"].Success ? m.Groups["unit"].Value.ToLowerInvariant() : "ms"; // default ms
        double ticks;
        switch (unit)
        {
            case "ns": ticks = value * TimeSpan.TicksPerMillisecond / 1_000_000.0; break;
            case "us":
            case "µs":
            case "μs": ticks = value * TimeSpan.TicksPerMillisecond / 1_000.0; break;
            case "ms": ticks = value * TimeSpan.TicksPerMillisecond; break;
            case "s": ticks = value * TimeSpan.TicksPerSecond; break;
            default: ticks = value * TimeSpan.TicksPerMillisecond; break;
        }
        var tsTicks = (long)Math.Round(Math.Clamp(ticks, 0, TimeSpan.TicksPerSecond));
        result = TimeSpan.FromTicks(tsTicks);
        return true;
    }

    private static bool TryParseSignedDuration(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true; // empty -> 0
        }
        var m = SignedDurationParseRegex.Match(text);
        if (!m.Success)
        {
            return false;
        }
        var numStr = m.Groups["num"].Value.Replace(',', '.');
        if (!double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }
        if (m.Groups["sign"].Success && m.Groups["sign"].Value == "-")
        {
            value = -value;
        }
        var unit = m.Groups["unit"].Success ? m.Groups["unit"].Value.ToLowerInvariant() : "ms";
        double ticks;
        switch (unit)
        {
            case "ns": ticks = value * TimeSpan.TicksPerMillisecond / 1_000_000.0; break;
            case "us":
            case "µs":
            case "μs": ticks = value * TimeSpan.TicksPerMillisecond / 1_000.0; break;
            case "ms": ticks = value * TimeSpan.TicksPerMillisecond; break;
            case "s": ticks = value * TimeSpan.TicksPerSecond; break;
            default: ticks = value * TimeSpan.TicksPerMillisecond; break;
        }
        var tsTicks = (long)Math.Round(Math.Clamp(ticks, -TimeSpan.TicksPerDay, TimeSpan.TicksPerDay));
        result = TimeSpan.FromTicks(tsTicks);
        return true;
    }

    private static string FormatDurationShort(TimeSpan span)
    {
        if (span == TimeSpan.Zero) return "0 ms";
        if (span.TotalSeconds >= 1) return $"{span.TotalSeconds:0.###} s";
        if (span.TotalMilliseconds >= 1) return $"{span.TotalMilliseconds:0.###} ms";
        var micro = span.TotalMilliseconds * 1000.0;
        if (micro >= 1) return $"{micro:0.###} µs";
        var nano = micro * 1000.0;
        return $"{nano:0.###} ns";
    }
}
