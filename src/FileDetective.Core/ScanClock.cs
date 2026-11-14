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

    /// <remarks>
    /// Stage 6 fix: this used <c>WaitHandle.WaitOne(duration)</c> once. <c>WaitOne(TimeSpan)</c> cuts the time to whole
    /// milliseconds, so every pause below 1 ms (the measured listing duration of a fast share) did not wait at all, and a
    /// wait may also end slightly early. The scan statistics still counted these pauses as taken. Now the wait is
    /// rounded up and repeated until the full measured duration has passed.
    /// </remarks>
    public void Pause(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            var left = duration - Stopwatch.GetElapsedTime(start);
            if (left <= TimeSpan.Zero)
            {
                break;
            }

            int milliseconds = (int)Math.Min(int.MaxValue - 1, Math.Ceiling(left.TotalMilliseconds));
            if (cancellationToken.WaitHandle.WaitOne(milliseconds))
            {
                break; // cancelled
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }
}
