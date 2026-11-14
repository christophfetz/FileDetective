namespace FileDetective.Core;

/// <summary>
/// Runs an action once, <see cref="Delay"/> after the first of a burst of <see cref="Trigger"/> calls. Further triggers
/// while the action is pending are absorbed, so a steady stream of triggers still runs the action at most once per delay
/// (it cannot be postponed forever). A trigger while the action runs schedules one more run.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly Action _action;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private bool _pending;
    private bool _disposed;

    public Debouncer(TimeSpan delay, Action action, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        Delay = delay;
        _action = action;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Delay { get; }

    /// <summary>Raised when the action threw.</summary>
    public event Action<Exception>? Failed;

    public void Trigger()
    {
        lock (_gate)
        {
            if (_pending || _disposed)
            {
                return;
            }

            _pending = true;
            _timer.Change(Delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = false;
        }

        _timer.Dispose();
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (!_pending || _disposed)
            {
                return;
            }

            _pending = false;
        }

        try
        {
            _action();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }
}
