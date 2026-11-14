using System.Security.Cryptography;

namespace FileDetective.Core;

/// <summary>Where a location is, for the duplicate search: only <see cref="LocalFixed"/> and <see cref="Removable"/> are ever read.</summary>
public enum DuplicateArea
{
    /// <summary>Local fixed drive: compared by content, files may be moved to the Recycle Bin.</summary>
    LocalFixed,

    /// <summary>Removable drive: compared by content, but never moved (there is no Recycle Bin).</summary>
    Removable,

    /// <summary>Share, mapped network drive or UNC path: only name and size from the directory, never opened.</summary>
    Share,
}

/// <summary>Step of the local duplicate check.</summary>
public enum DuplicatePhase
{
    /// <summary>Checking size, time and identity of the candidates (no content is read).</summary>
    Checking,

    /// <summary>Reading the first 64 KB.</summary>
    Partial,

    /// <summary>Reading whole files.</summary>
    Full,
}

/// <summary>Progress of the local check: files done of the files of this phase, bytes read so far in total.</summary>
public readonly record struct DuplicateProgress(DuplicatePhase Phase, int Done, int Total, long BytesRead);

/// <summary>Numbers of one local check.</summary>
public sealed class DuplicateStats
{
    /// <summary>Files from the directory with a size shared by at least one other file.</summary>
    public int Candidates { get; internal set; }

    /// <summary>Files only in the cloud: never read, never opened.</summary>
    public int CloudSkipped { get; internal set; }

    /// <summary>Symbolic links and other link-like files: never followed.</summary>
    public int LinksSkipped { get; internal set; }

    /// <summary>Further names of a file already in the comparison (hard links): not duplicates.</summary>
    public int HardLinks { get; internal set; }

    /// <summary>Files gone or changed since the last scan of the directory, or while they were read.</summary>
    public int Changed { get; internal set; }

    /// <summary>Files that could not be opened or read.</summary>
    public int Unreadable { get; internal set; }

    /// <summary>Times file content was read (the first 64 KB or the whole file; a file can count twice).</summary>
    public int FilesRead { get; internal set; }

    public long BytesRead { get; internal set; }

    /// <summary>Hashes taken from the table <c>hashes</c> instead of reading the file again.</summary>
    public int HashesReused { get; internal set; }
}

/// <summary>Result of <see cref="DuplicateFinder.FindLocal"/>: groups with the most wasted space first.</summary>
public sealed record DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, DuplicateStats Stats);

/// <summary>
/// The duplicate search (stage 10, PLAN.md 11.2).
/// <para>
/// Local: candidates are files of the same size (at least the minimum size) from the directory. Cloud-only files are
/// never opened. Hard links to the same file count once. Then a SHA-256 over the first 64 KB, and on equality a SHA-256
/// over the whole file. Hashes are kept in <c>hashes</c> and reused while size and time are unchanged.
/// </para>
/// <para>
/// Shares (also mapped network drives and UNC paths, whatever their stored kind): groups of the same name (search form)
/// and size from the directory only; no file there is probed, opened or read.
/// </para>
/// </summary>
public sealed class DuplicateFinder
{
    /// <summary>Length of the first, partial check.</summary>
    public const int PartialLength = 64 * 1024;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_OPEN: opening the file would fetch it from the cloud.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;

    private const uint NameSurrogateBit = 0x2000_0000;
    private const int ReadBufferSize = 1024 * 1024;
    private const int ResolveChunk = 2_000;

    private readonly IndexStore _store;
    private readonly ILocalFileAccess _files;
    private readonly DriveDiscovery _drives;

    public DuplicateFinder(IndexStore store, ILocalFileAccess files, DriveDiscovery drives)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(drives);
        _store = store;
        _files = files;
        _drives = drives;
    }

    /// <summary>
    /// True for files that must never be opened: cloud-only (<c>RecallOnDataAccess</c>, <c>Offline</c>) or fetched on
    /// open (<c>RecallOnOpen</c>).
    /// </summary>
    public static bool MustNotOpen(FileAttributes attributes)
        => SpaceCollector.IsCloudOnly(attributes) || (attributes & RecallOnOpen) != 0;

    /// <summary>
    /// Where a location lies. A location of kind Network, a UNC path, a mapped network drive and a path that cannot be
    /// described all count as share.
    /// </summary>
    public static DuplicateArea Classify(RootInfo root, DriveDiscovery drives)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(drives);
        if (root.Kind == RootKind.Network || root.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return DuplicateArea.Share;
        }

        var described = drives.Describe(root.Path);
        if (described is null || described.Kind == RootKind.Network)
        {
            return DuplicateArea.Share;
        }

        return root.Kind == RootKind.Removable || described.Kind == RootKind.Removable ? DuplicateArea.Removable : DuplicateArea.LocalFixed;
    }

    /// <summary>The local check by content. Runs long; call it on a low-priority thread (<see cref="LowPriorityWork"/>).</summary>
    /// <param name="progress">Called on the working thread; must be cheap and must not throw.</param>
    /// <exception cref="OperationCanceledException">Cancelled; hashes computed so far stay stored.</exception>
    public DuplicateScanResult FindLocal(
        long minSize, Action<DuplicateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var stats = new DuplicateStats();
        var areas = Areas();
        var local = areas.Where(a => a.Value != DuplicateArea.Share).Select(a => a.Key).ToHashSet();

        // 1. Candidates from the directory: same size, not cloud-only according to the directory.
        var bySize = new Dictionary<long, List<StoredEntry>>();
        _store.ReadFilesOfMinSize(
            minSize, local,
            e =>
            {
                if (!bySize.TryGetValue(e.Size, out var list))
                {
                    bySize[e.Size] = list = [];
                }

                list.Add(e);
            },
            cancellationToken);

        var candidates = new List<StoredEntry>();
        foreach (var list in bySize.Values.Where(l => l.Count > 1))
        {
            foreach (var e in list)
            {
                if (MustNotOpen(e.Attributes))
                {
                    stats.CloudSkipped++;
                }
                else
                {
                    candidates.Add(e);
                }
            }
        }

        stats.Candidates = candidates.Count + stats.CloudSkipped;
        var paths = ResolvePaths(candidates.Select(c => c.Id).ToList(), cancellationToken);

        // 2. Check: current size, time and attributes from the listing (no open), then the identity (hard links).
        var checkedFiles = new List<Candidate>();
        int done = 0;
        foreach (var e in candidates)
        {
            progress?.Invoke(new DuplicateProgress(DuplicatePhase.Checking, done++, candidates.Count, stats.BytesRead));
            cancellationToken.ThrowIfCancellationRequested();
            if (!paths.TryGetValue(e.Id, out var path))
            {
                stats.Changed++;
                continue;
            }

            var facts = _files.Probe(path);
            if (facts is not { } f || f.Size != e.Size)
            {
                stats.Changed++;
                continue;
            }

            if (MustNotOpen(f.Attributes))
            {
                stats.CloudSkipped++;
                continue;
            }

            if ((f.Attributes & FileAttributes.ReparsePoint) != 0 && (f.ReparseTag == 0 || (f.ReparseTag & NameSurrogateBit) != 0))
            {
                stats.LinksSkipped++;
                continue;
            }

            FileIdentity? identity;
            try
            {
                identity = _files.GetIdentity(path);
            }
            catch (Exception ex) when (IsFileError(ex))
            {
                identity = null;
            }

            if (identity is not { } id)
            {
                stats.Unreadable++;
                continue;
            }

            checkedFiles.Add(new Candidate(e, path, f, id, areas[e.RootId]));
        }

        var groups = new List<List<Candidate>>();
        foreach (var sameSize in checkedFiles.GroupBy(c => c.Facts.Size))
        {
            var distinct = new List<Candidate>();
            foreach (var sameFile in sameSize.GroupBy(c => c.Identity))
            {
                distinct.Add(sameFile.First());
                stats.HardLinks += sameFile.Count() - 1;
            }

            if (distinct.Count > 1)
            {
                groups.Add(distinct);
            }
        }

        // 3. Stored hashes, then the first 64 KB.
        var stored = _store.GetHashes(groups.SelectMany(g => g).Select(c => c.Entry.Id).ToList());
        foreach (var c in groups.SelectMany(g => g))
        {
            if (stored.TryGetValue(c.Entry.Id, out var hash) && hash.IsValidFor(c.Facts.Size, c.Facts.LastWriteUtcTicks))
            {
                c.Partial = hash.Partial;
                c.Full = hash.Full ?? (c.Facts.Size <= PartialLength ? hash.Partial : null);
            }
        }

        groups = Narrow(groups, DuplicatePhase.Partial, c => c.Partial, ReadPartial, stats, progress, cancellationToken);

        // 4. The whole file, only where the first 64 KB are equal.
        groups = Narrow(groups, DuplicatePhase.Full, c => c.Full, ReadFull, stats, progress, cancellationToken);

        var result = new List<DuplicateGroup>();
        foreach (var g in groups)
        {
            result.Add(new DuplicateGroup(
                g.Select(c => new DuplicateFile(
                    c.Entry.Id, c.Entry.RootId, c.Path, c.Facts.Size, c.Facts.LastWriteUtcTicks, c.Area == DuplicateArea.LocalFixed)),
                verified: true));
        }

        return new DuplicateScanResult(SortByWaste(result), stats);
    }

    /// <summary>
    /// Possible duplicates on shares: same name (search form) and same size, from the directory only. Nothing on the
    /// server is touched; the groups are not checked and cannot be marked.
    /// </summary>
    public IReadOnlyList<DuplicateGroup> FindOnShares(long minSize, CancellationToken cancellationToken = default)
    {
        var shares = Areas().Where(a => a.Value == DuplicateArea.Share).Select(a => a.Key).ToHashSet();
        var byKey = new Dictionary<(long Size, string Name), List<StoredEntry>>();
        _store.ReadFilesOfMinSize(
            minSize, shares,
            e =>
            {
                var key = (e.Size, Convert.ToHexString(SearchNormalizer.Normalize(e.Name)));
                if (!byKey.TryGetValue(key, out var list))
                {
                    byKey[key] = list = [];
                }

                list.Add(e);
            },
            cancellationToken);

        var multi = byKey.Values.Where(l => l.Count > 1).ToList();
        var paths = ResolvePaths(multi.SelectMany(l => l).Select(e => e.Id).ToList(), cancellationToken);
        var result = new List<DuplicateGroup>();
        foreach (var list in multi)
        {
            var files = list
                .Where(e => paths.ContainsKey(e.Id))
                .Select(e => new DuplicateFile(
                    e.Id, e.RootId, paths[e.Id], e.Size,
                    DateTime.UnixEpoch.AddSeconds(Math.Max(0, e.MTimeUnixSeconds)).Ticks, canRecycle: false))
                .ToList();
            if (files.Count > 1)
            {
                result.Add(new DuplicateGroup(files, verified: false));
            }
        }

        return SortByWaste(result);
    }

    private static List<DuplicateGroup> SortByWaste(List<DuplicateGroup> groups)
        => [.. groups.OrderByDescending(g => g.WastedBytes).ThenBy(g => g.Files[0].FullPath, StringComparer.OrdinalIgnoreCase)];

    private Dictionary<byte, DuplicateArea> Areas()
        => _store.GetRoots().Where(r => r.Enabled).ToDictionary(r => r.Id, r => Classify(r, _drives));

    private Dictionary<int, string> ResolvePaths(List<int> ids, CancellationToken cancellationToken)
    {
        var resolver = new DatabaseEntryResolver(_store.Database);
        var result = new Dictionary<int, string>();
        for (int start = 0; start < ids.Count; start += ResolveChunk)
        {
            var chunk = ids.GetRange(start, Math.Min(ResolveChunk, ids.Count - start));
            var details = resolver.Resolve(chunk, cancellationToken);
            for (int i = 0; i < chunk.Count; i++)
            {
                if (details[i] is { IsDirectory: false } d)
                {
                    result[chunk[i]] = d.FullPath;
                }
            }
        }

        return result;
    }

    /// <summary>Gives every file of the groups a hash (stored or read), then splits the groups by it.</summary>
    private List<List<Candidate>> Narrow(
        List<List<Candidate>> groups,
        DuplicatePhase phase,
        Func<Candidate, byte[]?> hashOf,
        Func<Candidate, DuplicateStats, CancellationToken, bool> read,
        DuplicateStats stats,
        Action<DuplicateProgress>? progress,
        CancellationToken cancellationToken)
    {
        int total = groups.Sum(g => g.Count);
        int done = 0;
        var result = new List<List<Candidate>>();
        foreach (var group in groups)
        {
            var ok = new List<Candidate>();
            foreach (var c in group)
            {
                progress?.Invoke(new DuplicateProgress(phase, done++, total, stats.BytesRead));
                cancellationToken.ThrowIfCancellationRequested();
                if (hashOf(c) is not null)
                {
                    stats.HashesReused++;
                    ok.Add(c);
                }
                else if (read(c, stats, cancellationToken))
                {
                    ok.Add(c);
                }
            }

            foreach (var same in ok.GroupBy(c => Convert.ToHexString(hashOf(c)!)))
            {
                if (same.Count() > 1)
                {
                    result.Add([.. same]);
                }
            }
        }

        progress?.Invoke(new DuplicateProgress(phase, total, total, stats.BytesRead));
        return result;
    }

    private bool ReadPartial(Candidate c, DuplicateStats stats, CancellationToken cancellationToken)
    {
        byte[]? hash = Read(c, stats, cancellationToken, whole: false);
        if (hash is null)
        {
            return false;
        }

        c.Partial = hash;
        c.Full = c.Facts.Size <= PartialLength ? hash : null; // the first 64 KB are the whole file
        Save(c);
        return true;
    }

    private bool ReadFull(Candidate c, DuplicateStats stats, CancellationToken cancellationToken)
    {
        byte[]? hash = Read(c, stats, cancellationToken, whole: true);
        if (hash is null)
        {
            return false;
        }

        c.Full = hash;
        Save(c);
        return true;
    }

    private void Save(Candidate c)
        => _store.SaveHash(new StoredHash(c.Entry.Id, c.Facts.Size, c.Facts.LastWriteUtcTicks, c.Partial, c.Full));

    /// <summary>Reads the start or the whole file and checks afterwards that it did not change meanwhile.</summary>
    private byte[]? Read(Candidate c, DuplicateStats stats, CancellationToken cancellationToken, bool whole)
    {
        byte[] hash;
        try
        {
            stats.FilesRead++;
            using var stream = _files.OpenRead(c.Path);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[whole ? ReadBufferSize : PartialLength];
            long remaining = whole ? long.MaxValue : PartialLength;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (n == 0)
                {
                    break;
                }

                sha.AppendData(buffer, 0, n);
                stats.BytesRead += n;
                remaining -= n;
            }

            hash = sha.GetHashAndReset();
        }
        catch (Exception ex) when (IsFileError(ex))
        {
            stats.Unreadable++;
            return null;
        }

        if (_files.Probe(c.Path) is not { } now || now.Size != c.Facts.Size || now.LastWriteUtcTicks != c.Facts.LastWriteUtcTicks)
        {
            stats.Changed++;
            return null;
        }

        return hash;
    }

    private static bool IsFileError(Exception ex)
        => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException;

    private sealed class Candidate(StoredEntry entry, string path, LocalFileFacts facts, FileIdentity identity, DuplicateArea area)
    {
        public StoredEntry Entry { get; } = entry;

        public string Path { get; } = path;

        public LocalFileFacts Facts { get; } = facts;

        public FileIdentity Identity { get; } = identity;

        public DuplicateArea Area { get; } = area;

        public byte[]? Partial { get; set; }

        public byte[]? Full { get; set; }
    }
}
