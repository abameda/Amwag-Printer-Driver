using Amwag.PrinterSetup.Core.Abstractions;

namespace Amwag.PrinterSetup.Testing;

/// <summary>Deterministic clock: <see cref="Sleep"/> advances time instantly and runs the callbacks that became due.</summary>
public sealed class FakeClock : IClock
{
    public static readonly DateTimeOffset DefaultStart = new(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(2));

    private readonly object _gate = new();
    private readonly List<(DateTimeOffset Due, long Order, Action Callback)> _scheduled = [];
    private DateTimeOffset _now;
    private TimeSpan _totalSlept;
    private int _sleepCount;
    private long _order;

    public FakeClock()
        : this(DefaultStart)
    {
    }

    public FakeClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset Now
    {
        get { lock (_gate) return _now; }
    }

    /// <summary>Sum of all durations passed to <see cref="Sleep"/>.</summary>
    public TimeSpan TotalSlept
    {
        get { lock (_gate) return _totalSlept; }
    }

    public int SleepCount
    {
        get { lock (_gate) return _sleepCount; }
    }

    public void Sleep(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _totalSlept += duration;
            _sleepCount++;
        }
        Advance(duration);
    }

    /// <summary>Moves time forward and runs every callback due by then, in schedule order.</summary>
    public void Advance(TimeSpan duration)
    {
        List<Action> due;
        lock (_gate)
        {
            _now += duration;
            due = TakeDue();
        }

        // Callbacks run outside the lock: they typically mutate other fakes.
        foreach (var callback in due)
            callback();
    }

    /// <summary>Runs <paramref name="callback"/> once the clock has advanced by <paramref name="delay"/> (immediately when not positive).</summary>
    public void Schedule(TimeSpan delay, Action callback)
    {
        if (delay <= TimeSpan.Zero)
        {
            callback();
            return;
        }

        lock (_gate)
            _scheduled.Add((_now + delay, _order++, callback));
    }

    private List<Action> TakeDue()
    {
        var due = _scheduled.Where(s => s.Due <= _now).OrderBy(s => s.Due).ThenBy(s => s.Order).ToList();
        _scheduled.RemoveAll(s => s.Due <= _now);
        return due.Select(s => s.Callback).ToList();
    }
}
