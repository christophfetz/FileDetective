namespace FileDetective.Core;

/// <summary>Result of <see cref="DirectoryRebuilder.Rebuild"/>.</summary>
/// <param name="Removed">Entries that were deleted.</param>
/// <param name="Queued">Locations queued for the new scan.</param>
public readonly record struct RebuildResult(int Removed, int Queued);

/// <summary>
/// "Rebuild directory" (PLAN.md 8.5): brings every location back to "never scanned" and queues it for a complete new scan.
/// </summary>
/// <remarks>
/// Order of work: (1) for all locations, drop the queued scans, cancel the running one and wait for it, and reject new
/// requests (<see cref="ScanQueue.CancelAndBlock(IReadOnlyCollection{byte}, TimeSpan?)"/>), then stop the watchers; (2) take the lock of every location, so
/// nothing else works on the entries any more; (3) <see cref="IndexStore.ResetAllEntries"/> (one transaction; on failure
/// the old directory stays and the exception is passed on); (4) release everything, resume the watchers and queue every
/// enabled location with <see cref="ScanTrigger.Manual"/>. A manual request does not count against the daily limit of a
/// share, and the scanner keeps its pauses on shares, so rebuilding is as gentle on a server as any other scan. The
/// locations and the usage history are not touched.
/// </remarks>
public static class DirectoryRebuilder
{
    /// <param name="store">The directory.</param>
    /// <param name="queue">The scan queue of the program.</param>
    /// <param name="locks">The location locks that scans and the watcher hold.</param>
    /// <param name="unwatch">Stops the watcher of a location (null if there is none).</param>
    /// <param name="resumeWatching">Starts the watchers again for the given locations (null if there are none).</param>
    /// <param name="afterReset">Runs while everything is still locked, after the entries were deleted (refresh caches).</param>
    /// <param name="log">Receives progress lines.</param>
    public static RebuildResult Rebuild(
        IndexStore store,
        ScanQueue queue,
        LocationLocks locks,
        Action<byte>? unwatch = null,
        Action<IReadOnlyList<RootInfo>>? resumeWatching = null,
        Action? afterReset = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(locks);

        var roots = store.GetRoots();
        var handles = new List<IDisposable>();
        int removed;
        try
        {
            // All locations at once: waiting for the running scan must not let the worker start the next waiting one.
            handles.Add(queue.CancelAndBlock([.. roots.Select(r => r.Id)]));
            foreach (var root in roots)
            {
                unwatch?.Invoke(root.Id);
            }

            // Ascending order, always: the same order everywhere keeps two callers from waiting for each other.
            foreach (var root in roots.OrderBy(r => r.Id))
            {
                handles.Add(locks.Acquire(root.Id));
            }

            log?.Invoke($"Rebuilding the directory: {roots.Count} locations.");
            removed = store.ResetAllEntries();
            log?.Invoke($"Directory emptied: {removed} entries removed.");
            afterReset?.Invoke();
        }
        finally
        {
            for (int i = handles.Count - 1; i >= 0; i--)
            {
                handles[i].Dispose();
            }

            resumeWatching?.Invoke(store.GetRoots());
        }

        int queued = 0;
        foreach (var root in store.GetRoots())
        {
            if (root.Enabled && queue.Enqueue(new ScanRequest(root.Id, root.Kind, ScanTrigger.Manual)))
            {
                queued++;
            }
        }

        return new RebuildResult(removed, queued);
    }
}
