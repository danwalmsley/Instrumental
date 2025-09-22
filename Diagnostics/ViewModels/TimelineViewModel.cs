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
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _currentTime;

    public TimelineViewModel()
    {
        _currentTime = DateTimeOffset.UtcNow;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => CurrentTime = DateTimeOffset.UtcNow);
        _timer.Start();
    }

    public ObservableCollection<TimelineTrack> Tracks { get; } = new();

    public TimeSpan VisibleDuration { get; set; } = TimeSpan.FromSeconds(30);

    public DateTimeOffset CurrentTime
    {
        get => _currentTime;
        private set => SetProperty(ref _currentTime, value);
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

        TrimOutOfRange(CurrentTime - TimeSpan.FromTicks(VisibleDuration.Ticks * 2));
    }

    public void ApplyEventStop(Guid eventId, DateTimeOffset timestamp)
    {
        if (_eventsById.TryGetValue(eventId, out var timelineEvent))
        {
            timelineEvent.Complete(timestamp);
        }
    }

    public void TrimOutOfRange(DateTimeOffset olderThan)
    {
        foreach (var track in Tracks)
        {
            TrimCollection(track.Events, olderThan);
        }
    }

    private void TrimCollection(ICollection<TimelineEvent> events, DateTimeOffset olderThan)
    {
        var snapshot = events.ToList();
        foreach (var evt in snapshot)
        {
            if ((evt.End ?? CurrentTime) < olderThan)
            {
                RemoveEvent(evt);
            }
            else if (evt.Children.Count > 0)
            {
                TrimCollection(evt.Children, olderThan);
            }
        }
    }

    private void RemoveEvent(TimelineEvent timelineEvent)
    {
        RemoveEventFromContainers(timelineEvent);
        _eventsById.Remove(timelineEvent.Id);
        _eventParentById.Remove(timelineEvent.Id);
        _eventTrackById.Remove(timelineEvent.Id);
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
}
