using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using Diagnostics.EventBroadcast;
using Diagnostics.EventBroadcast.Messaging;

namespace Diagnostics.Avalonia;

public sealed class AvaloniaDiagnosticsInstrumentor
{
    private readonly struct BatchState
    {
        public readonly Guid EventId;
        public readonly DateTimeOffset Start;
        public readonly DispatcherPriority Priority;
        public BatchState(Guid id, DateTimeOffset start, DispatcherPriority priority)
        {
            EventId = id; Start = start; Priority = priority;
        }
    }

    private readonly Dictionary<DispatcherPriority, BatchState> _openBatches = new();
    private readonly Dictionary<Guid, (string Track,string Label,Color Color,DateTimeOffset Start,Guid? Parent)> _activeEvents = new();
    private readonly Dictionary<DispatcherPriority, TimeSpan> _priorityTimes = new();
    private long _lastScheduledTick = -1; // replaces boolean gating
    private long _renderTick;
    private Guid? _activeRenderId;
    private Guid? _activeParentId;
    private bool _frameOpen; // track if current frame parent still open
    private static readonly DispatcherPriority LastPriorityCloseFallback = DispatcherPriority.ContextIdle;

    private static T GetRequiredService<T>() where T:class =>
        (T)(AvaloniaLocator.Current.GetService(typeof(T)) ?? throw new InvalidOperationException($"Service {typeof(T)} missing"));

    private static string Hex(Color c)=> c.A<byte.MaxValue?$"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}":$"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private Guid StartEvent(string track,string label,Color color, Guid? id=null, Guid? parent=null, DateTimeOffset? ts=null, string? metadata=null)
    {
        var eid = id ?? Guid.NewGuid();
        var t = ts ?? DateTimeOffset.UtcNow;
        var finalLabel = metadata != null ? $"{label} {metadata}" : label;
        _activeEvents[eid]=(track,finalLabel,color,t,parent);
        return eid;
    }

    private void CompleteEvent(TimelineEventBroadcaster b, Guid id, DateTimeOffset end)
    {
        if(!_activeEvents.TryGetValue(id,out var info)) return;
        b.EnqueueComplete(new TimelineEventComplete(
            id, info.Track, info.Label, Hex(info.Color), info.Start, end, info.Parent));
        _activeEvents.Remove(id);
    }

    private string FormatPriorityDuration(TimeSpan time)
    {
        if (time.TotalMilliseconds >= 1)
        {
            return $"{time.TotalMilliseconds:F3}ms"; // milliseconds
        }
        var micro = time.TotalMilliseconds * 1000.0; // microseconds
        if (micro >= 1)
        {
            return $"{micro:F1}µs"; // show one decimal place for microseconds >=1
        }
        // fall back to nanoseconds for extremely small intervals (1 tick = 100ns)
        var nanos = time.Ticks * 100; // nanoseconds
        return $"{nanos}ns";
    }

    private static readonly Dictionary<DispatcherPriority, Color> PriorityColors = new()
    {
        { DispatcherPriority.Normal, Colors.SteelBlue },
        { DispatcherPriority.Render, Colors.MediumPurple },
        { DispatcherPriority.Input, Colors.OrangeRed },
        { DispatcherPriority.Background, Colors.DimGray }
    };

    private static Color GetPriorityColor(DispatcherPriority p)
        => PriorityColors.TryGetValue(p, out var c) ? c : Colors.Gray;

    public async Task InstrumentAvalonia(
        TimelineEventBroadcaster broadcaster,
        TimeSpan? duration = null,
        CancellationToken ct = default,
        IReadOnlyList<DispatcherPriority>? prioritiesToMeasure = null)
    {
        if (broadcaster == null) throw new ArgumentNullException(nameof(broadcaster));

        // Priorities to measure
        var priorityChain = prioritiesToMeasure ?? new[]
        {
            // Descending order so each segment closes when the next LOWER priority runs.
            // WPF/Avalonia dispatcher processes higher numeric priorities first (Send > Normal > Render > Input ...)
            // So we list from highest to lowest we want to measure to ensure: Start at p, Close when next lower executes.
            DispatcherPriority.Normal,
            DispatcherPriority.Render,
            DispatcherPriority.Input
        };

        var dispatcher = Dispatcher.UIThread;
        var renderTimer = GetRequiredService<IRenderTimer>();

        void CloseBatchIfOpen(DispatcherPriority p, DateTimeOffset now)
        {
            if (_openBatches.TryGetValue(p, out var batch))
            {
                var elapsed = now - batch.Start;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (!_priorityTimes.ContainsKey(p))
                    _priorityTimes[p] = TimeSpan.Zero;
                _priorityTimes[p] = _priorityTimes[p].Add(elapsed);

                // Update child event label with duration then complete it
                if (_activeEvents.TryGetValue(batch.EventId, out var evtInfo))
                {
                    var newLabel = $"{evtInfo.Label} {FormatPriorityDuration(elapsed)}";
                    _activeEvents[batch.EventId] = (evtInfo.Track, newLabel, evtInfo.Color, evtInfo.Start, evtInfo.Parent);
                    CompleteEvent(broadcaster, batch.EventId, now);
                }
                _openBatches.Remove(p);
            }
        }

        void FinalizePreviousFrame(DateTimeOffset now)
        {
            if (!_frameOpen || !_activeParentId.HasValue)
            {
                _priorityTimes.Clear();
                return;
            }
            // Close any still-open batches
            foreach (var kv in _openBatches)
            {
                var p = kv.Key;
                CloseBatchIfOpen(p, now);
            }
            _openBatches.Clear();
            var metadata = CreatePriorityMetadata();
            if (_activeEvents.TryGetValue(_activeParentId.Value, out var eventInfo))
            {
                _activeEvents[_activeParentId.Value] = (eventInfo.Track, $"{eventInfo.Label} {metadata}", eventInfo.Color, eventInfo.Start, eventInfo.Parent);
            }
            CompleteEvent(broadcaster, _activeParentId.Value, now);
            _activeParentId = null;
            _frameOpen = false;
            _priorityTimes.Clear();
        }

        void ScheduleFrameFinalizationAtSystemIdle()
        {
            dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (!_frameOpen) return;
                FinalizePreviousFrame(DateTimeOffset.UtcNow);
            }, DispatcherPriority.SystemIdle);
        }

        void ScheduleFences(long tick)
        {
            if (_lastScheduledTick == tick) return;
            _lastScheduledTick = tick;
            var now = DateTimeOffset.UtcNow;
            FinalizePreviousFrame(now); // finalize previous frame if still open

            // Parent frame
            dispatcher.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                var startNow = DateTimeOffset.UtcNow;
                _activeParentId = StartEvent("Dispatcher", $"Tick {tick} Frame", Colors.DarkSlateGray, Guid.NewGuid(), null, startNow);
                _frameOpen = true;
            }, DispatcherPriority.Send);

            // Child priority batches
            for (int i = 0; i < priorityChain.Count; i++)
            {
                var p = priorityChain[i];
                var isLast = i == priorityChain.Count - 1;
                DispatcherPriority? nextLower = isLast ? (DispatcherPriority?)null : priorityChain[i + 1];

                dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || !_frameOpen) return;
                    if (_activeParentId.HasValue)
                    {
                        var startTime = DateTimeOffset.UtcNow;
                        var childId = StartEvent("Dispatcher", p.ToString(), GetPriorityColor(p), Guid.NewGuid(), _activeParentId.Value, startTime);
                        _openBatches[p] = new BatchState(childId, startTime, p);
                    }
                }, p);

                if (!isLast && nextLower.HasValue)
                {
                    dispatcher.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested || !_frameOpen) return;
                        CloseBatchIfOpen(p, DateTimeOffset.UtcNow);
                    }, nextLower.Value);
                }
                else if (isLast)
                {
                    // Close the final priority earlier than SystemIdle if possible
                    if (LastPriorityCloseFallback != DispatcherPriority.SystemIdle)
                    {
                        dispatcher.InvokeAsync(() =>
                        {
                            if (ct.IsCancellationRequested || !_frameOpen) return;
                            CloseBatchIfOpen(p, DateTimeOffset.UtcNow);
                        }, LastPriorityCloseFallback);
                    }
                }
            }

            // Always schedule frame finalization at SystemIdle after child closures
            ScheduleFrameFinalizationAtSystemIdle();
        }

        string CreatePriorityMetadata()
        {
            var parts = new List<string>();
            foreach (var p in priorityChain)
            {
                if (_priorityTimes.TryGetValue(p, out var time) && time > TimeSpan.Zero)
                {
                    parts.Add($"●{p}:{FormatPriorityDuration(time)}");
                }
            }
            return parts.Count > 0 ? $"[{string.Join(" ", parts)}]" : "";
        }

        void OnRender(TimeSpan _)
        {
            if (ct.IsCancellationRequested) return;
            var now = DateTimeOffset.UtcNow;
            if (_activeRenderId is { } rid)
            {
                CompleteEvent(broadcaster, rid, now);
                _activeRenderId = null;
            }
            var tick = Interlocked.Increment(ref _renderTick);
            _activeRenderId = StartEvent("Render", $"Render Tick {tick}", Colors.Purple, Guid.NewGuid(), null, now);
            ScheduleFences(tick);
        }

        renderTimer.Tick += OnRender;

        try
        {
            if (duration.HasValue)
            {
                await Task.Delay(duration.Value, ct).ConfigureAwait(false);
            }
            else
            {
                // Run indefinitely until cancellation
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            renderTimer.Tick -= OnRender;
            var endNow = DateTimeOffset.UtcNow;
            FinalizePreviousFrame(endNow);
            foreach (var kv in _openBatches.Values)
            {
                CompleteEvent(broadcaster, kv.EventId, endNow);
            }
            _openBatches.Clear();
            if (_activeRenderId is { } rid)
            {
                CompleteEvent(broadcaster, rid, endNow);
                _activeRenderId = null;
            }
            foreach (var id in new List<Guid>(_activeEvents.Keys))
            {
                CompleteEvent(broadcaster, id, endNow);
            }
            _activeEvents.Clear();
        }
    }
}
