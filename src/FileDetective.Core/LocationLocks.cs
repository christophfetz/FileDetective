namespace FileDetective.Core;

/// <summary>
/// One exclusive lock per location. Everything that changes the entries of a location by comparing the file system
/// with the index (a whole scan from the <see cref="ScanQueue"/>, the work of the <see cref="LocalWatcher"/> including
/// its subtree scans, removing the location) holds the lock of that location while it works.
/// </summary>
/// <remarks>
/// Why: the scanner reads the known children of a folder and writes the differences later in a batch. If another writer
/// inserted the same child in between, the child would end up in the index twice. The <see cref="IndexStore"/> makes each
/// single write safe, the lock makes the read-compare-write sequences of one location mutually exclusive. Different
/// locations never block each other, so a long share scan does not delay local watcher updates.
/// </remarks>
public sealed class LocationLocks
{
    private readonly SemaphoreSlim[] _locks = new SemaphoreSlim[256];

    /// <summary>Waits for the lock of <paramref name="rootId"/> and returns a handle that releases it.</summary>
    /// <exception cref="OperationCanceledException">Cancelled while waiting.</exception>
    public IDisposable Acquire(byte rootId, CancellationToken cancellationToken = default)
    {
        var gate = Gate(rootId);
        gate.Wait(cancellationToken);
        return new Releaser(gate);
    }

    /// <summary>Tries to take the lock without waiting longer than <paramref name="timeout"/>; null if it is taken.</summary>
    public IDisposable? TryAcquire(byte rootId, TimeSpan timeout)
    {
        var gate = Gate(rootId);
        return gate.Wait(timeout) ? new Releaser(gate) : null;
    }

    /// <summary>True while someone holds the lock of <paramref name="rootId"/> (for tests and diagnostics).</summary>
    public bool IsHeld(byte rootId) => Gate(rootId).CurrentCount == 0;

    private SemaphoreSlim Gate(byte rootId)
    {
        var gate = Volatile.Read(ref _locks[rootId]);
        if (gate is null)
        {
            Interlocked.CompareExchange(ref _locks[rootId], new SemaphoreSlim(1, 1), null);
            gate = Volatile.Read(ref _locks[rootId])!;
        }

        return gate;
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
