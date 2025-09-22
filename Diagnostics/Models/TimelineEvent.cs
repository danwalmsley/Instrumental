using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Diagnostics.Models;

public class TimelineEvent : INotifyPropertyChanged
{
    private DateTimeOffset? _end;

    public TimelineEvent(Guid id, DateTimeOffset start, string label, Color color, Guid? parentId = null, CornerRadius? cornerRadius = null)
    {
        Id = id;
        ParentId = parentId;
        Start = start;
        Label = label;
        Fill = new ImmutableSolidColorBrush(color);
        CornerRadius = cornerRadius ?? new CornerRadius(4);
    }

    public Guid Id { get; }

    public Guid? ParentId { get; }

    public DateTimeOffset Start { get; }

    public string Label { get; }

    public IBrush Fill { get; }

    public CornerRadius CornerRadius { get; }

    public ObservableCollection<TimelineEvent> Children { get; } = new();

    public DateTimeOffset? End
    {
        get => _end;
        private set
        {
            if (_end != value)
            {
                _end = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(IsActive));
            }
        }
    }

    public bool IsActive => !_end.HasValue;

    public TimeSpan Duration => (_end ?? DateTimeOffset.UtcNow) - Start;

    public void Complete(DateTimeOffset timestamp)
    {
        if (_end.HasValue && timestamp <= _end)
        {
            return;
        }

        End = timestamp;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
