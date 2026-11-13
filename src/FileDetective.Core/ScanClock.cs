using System.Diagnostics;

namespace FileDetective.Core;

/// <summary>
/// Time source and pause for the <see cref="Scanner"/>, injectable so that tests check listing durations and pauses
/// (PLAN.md 7.4) without real waiting.
/// </summary>
public interface IScanClock
{
    /// <summary>Current time (UTC) for the scan times stored in <c>roots</c>.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic time since an arbitrary start, for measuring durations.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Waits for <paramref name="duration"/>; throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    void Pause(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>The real clock: system time, <see cref="Stopwatch"/>, and a cancellable wait.</summary>
public sealed class SystemScanClock : IScanClock
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public static SystemScanClock Instance { get; } = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);

    public void Pause(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration > TimeSpan.Zero)
        {
            cancellationToken.WaitHandle.WaitOne(duration);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
