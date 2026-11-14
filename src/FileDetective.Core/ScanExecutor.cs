namespace FileDetective.Core;

/// <summary>
/// Runs one whole-location scan for the <see cref="ScanQueue"/>: takes the lock of the location (so the
/// <see cref="LocalWatcher"/> never works on it at the same time), stores start time and attempt counter
/// (<see cref="RefreshScheduler.PrepareStart"/>) and runs <see cref="Scanner.ScanRoot"/>.
/// </summary>
public sealed class ScanExecutor
{
    private readonly IndexStore _store;
    private readonly LocationLocks _locks;
    private readonly Func<ScanRequest, Scanner> _createScanner;
    private readonly TimeProvider _time;

    /// <param name="createScanner">Creates the scanner for one request (settings such as exclusions are read there).</param>
    public ScanExecutor(IndexStore store, LocationLocks locks, Func<ScanRequest, Scanner> createScanner, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(createScanner);
        _store = store;
        _locks = locks;
        _createScanner = createScanner;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Runs the scan; null if the location does not exist (any more).</summary>
    public ScanStatistics? Run(ScanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var hold = _locks.Acquire(request.RootId, cancellationToken);
        if (_store.GetRoot(request.RootId) is not { } root)
        {
            return null;
        }

        _store.UpdateRoot(RefreshScheduler.PrepareStart(root, request, _time.GetUtcNow(), _time.LocalTimeZone));
        return _createScanner(request).ScanRoot(root.Id, cancellationToken);
    }
}
