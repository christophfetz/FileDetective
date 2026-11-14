using System.Diagnostics;

namespace FileDetective.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose time only moves on <see cref="Advance"/>. Timers fire synchronously inside
/// <see cref="Advance"/>, in the order of their due times, with the clock set to their due time.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly TimeZoneInfo _zone;
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start, TimeZoneInfo? zone = null)
    {
        _now = start;
        _zone = zone ?? TimeZoneInfo.Utc;
    }

    /// <summary>A fixed zone one hour ahead of UTC without daylight saving (calendar-day tests independent of the machine).</summary>
    public static TimeZoneInfo UtcPlusOne { get; } =
        TimeZoneInfo.CreateCustomTimeZone("FileDetective-Test+01", TimeSpan.FromHours(1), "Test+01", "Test+01");

    public override TimeZoneInfo LocalTimeZone => _zone;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the time forward and fires every timer that becomes due on the way.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Due = next.Period > TimeSpan.Zero ? _now + next.Period : null;
            }

            next.Callback(next.State);
        }
    }

    /// <summary>Moves the time to a point (must not be in the past).</summary>
    public void AdvanceTo(DateTimeOffset when) => Advance(when - GetUtcNow());

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                if (!owner._timers.Contains(this))
                {
                    owner._timers.Add(this);
                }
            }

            return true;
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Waits for an end state by polling instead of fixed sleeps, so tests are fast and do not flicker.</summary>
internal static class Poll
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Polls every 20 ms until <paramref name="condition"/> holds; fails the test after the time limit.</summary>
    public static void Until(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        var limit = timeout ?? DefaultTimeout;
        while (!condition())
        {
            if (watch.Elapsed > limit)
            {
                Assert.Fail($"Timed out after {limit.TotalSeconds:F0} s waiting for: {what}");
            }

            Thread.Sleep(20);
        }
    }
}
