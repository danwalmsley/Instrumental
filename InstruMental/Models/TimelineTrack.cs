using System.Collections.ObjectModel;

namespace InstruMental.Models;

public class TimelineTrack
{
    public TimelineTrack(string name)
    {
        Name = name;
    }

    public string Name { get; }

    public ObservableCollection<TimelineEvent> Events { get; } = new();
}
