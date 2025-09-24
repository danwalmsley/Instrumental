using System.Collections.ObjectModel;

namespace InstrumentScope.Models;

public class TimelineTrack
{
    public TimelineTrack(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ObservableCollection<TimelineEvent> Events { get; } = new();
}
