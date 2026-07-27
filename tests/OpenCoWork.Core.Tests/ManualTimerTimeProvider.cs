namespace OpenCoWork.Core.Tests;

internal sealed class ManualTimerTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    public void Advance(TimeSpan value)
    {
        _utcNow += value;
        ManualTimer[] timers;
        lock (_gate)
        {
            timers = _timers.ToArray();
        }

        foreach (var timer in timers)
        {
            timer.Advance(value);
        }
    }

    private sealed class ManualTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) : ITimer
    {
        private TimeSpan _remaining = dueTime;
        private TimeSpan _period = period;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _remaining = dueTime;
            _period = period;
            return true;
        }

        public void Advance(TimeSpan value)
        {
            if (_disposed || _remaining == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            _remaining -= value;
            while (_remaining <= TimeSpan.Zero && !_disposed)
            {
                callback(state);
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    _remaining = Timeout.InfiniteTimeSpan;
                    return;
                }

                _remaining += _period;
            }
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
