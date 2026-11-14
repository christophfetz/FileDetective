namespace FileDetective.Core;

/// <summary>One file of a duplicate group (stage 10, PLAN.md 11.2). Marks are changed through its <see cref="Group"/> only.</summary>
public sealed class DuplicateFile
{
    internal DuplicateFile(int entryId, byte rootId, string fullPath, long size, long mtimeTicks, bool canRecycle)
    {
        EntryId = entryId;
        RootId = rootId;
        FullPath = fullPath;
        Size = size;
        MTimeTicks = mtimeTicks;
        CanRecycle = canRecycle;
    }

    public int EntryId { get; }

    public byte RootId { get; }

    public string FullPath { get; }

    public string Name => Path.GetFileName(FullPath);

    public string Folder => Path.GetDirectoryName(FullPath) ?? FullPath;

    /// <summary>Size in bytes: the one checked when comparing (local), or the one in the directory (shares).</summary>
    public long Size { get; }

    /// <summary>Last write time (UTC ticks) as seen when comparing (local) or as in the directory (shares, whole seconds).</summary>
    public long MTimeTicks { get; }

    public DateTime LastWriteUtc => new(MTimeTicks, DateTimeKind.Utc);

    /// <summary>True only for files on a local fixed drive: never on shares, never on removable drives (no Recycle Bin there).</summary>
    public bool CanRecycle { get; }

    public bool IsMarked { get; internal set; }

    public DuplicateGroup Group { get; internal set; } = null!;
}

/// <summary>
/// Files with the same content (local, <see cref="IsVerified"/>) or with the same name and size (shares: possible
/// duplicates, not checked). Enforces the rule of PLAN.md 11.2 that at least one file of a group always stays unmarked:
/// no method can mark the last unmarked file, and nothing in an unchecked group can be marked at all.
/// </summary>
public sealed class DuplicateGroup
{
    private readonly List<DuplicateFile> _files;

    public DuplicateGroup(IEnumerable<DuplicateFile> files, bool verified)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = [.. files.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)];
        if (_files.Count < 2)
        {
            throw new ArgumentException("A group has at least two files.", nameof(files));
        }

        IsVerified = verified;
        foreach (var f in _files)
        {
            f.Group = this;
            f.IsMarked = false;
        }
    }

    public IReadOnlyList<DuplicateFile> Files => _files;

    /// <summary>True if the contents were compared; false for "possible duplicates, not checked" on shares.</summary>
    public bool IsVerified { get; }

    public long Size => _files[0].Size;

    /// <summary>Space taken by the extra copies: size × (number of files − 1).</summary>
    public long WastedBytes => Size * Math.Max(0, _files.Count - 1);

    public int MarkedCount => _files.Count(f => f.IsMarked);

    public long MarkedBytes => _files.Where(f => f.IsMarked).Sum(f => f.Size);

    /// <summary>True if <paramref name="file"/> may be marked now.</summary>
    public bool CanMark(DuplicateFile file)
        => IsVerified && file is { CanRecycle: true, IsMarked: false } && ReferenceEquals(file.Group, this)
           && _files.Count(f => !f.IsMarked) > 1;

    /// <summary>Marks or unmarks one file. Returns false (and changes nothing) if marking is not allowed.</summary>
    public bool SetMarked(DuplicateFile file, bool marked)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!ReferenceEquals(file.Group, this))
        {
            return false;
        }

        if (!marked)
        {
            file.IsMarked = false;
            return true;
        }

        if (file.IsMarked)
        {
            return true;
        }

        if (!CanMark(file))
        {
            return false;
        }

        file.IsMarked = true;
        return true;
    }

    /// <summary>"Mark all but the newest": the newest file (last write time; the first one on a tie) stays unmarked.</summary>
    /// <returns>True if the group was changed.</returns>
    public bool MarkAllExceptNewest()
    {
        var newest = _files.Aggregate((a, b) => b.MTimeTicks > a.MTimeTicks ? b : a);
        return MarkAllExcept([newest]);
    }

    /// <summary>
    /// "Mark all but those in this folder": files in <paramref name="folder"/> or below stay unmarked, the others are
    /// marked. A group without any file there is left as it is (marking all would break the rule).
    /// </summary>
    /// <returns>True if the group was changed.</returns>
    public bool MarkAllExceptIn(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var keep = _files.Where(f => IsInFolder(f.FullPath, folder)).ToList();
        return keep.Count > 0 && MarkAllExcept(keep);
    }

    public void ClearMarks()
    {
        foreach (var f in _files)
        {
            f.IsMarked = false;
        }
    }

    /// <summary>True if <paramref name="path"/> lies in <paramref name="folder"/> or one of its subfolders.</summary>
    public static bool IsInFolder(string path, string folder)
    {
        string dir = (Path.GetDirectoryName(path) ?? string.Empty).TrimEnd('\\', '/');
        string f = folder.TrimEnd('\\', '/');
        return dir.Equals(f, StringComparison.OrdinalIgnoreCase)
            || (dir.Length > f.Length && dir.StartsWith(f, StringComparison.OrdinalIgnoreCase) && dir[f.Length] is '\\' or '/');
    }

    internal void Remove(DuplicateFile file) => _files.Remove(file);

    private bool MarkAllExcept(IReadOnlyCollection<DuplicateFile> keep)
    {
        if (!IsVerified || keep.Count == 0)
        {
            return false;
        }

        bool changed = false;
        foreach (var f in _files)
        {
            bool marked = f.CanRecycle && !keep.Contains(f);
            changed |= f.IsMarked != marked;
            f.IsMarked = marked;
        }

        return changed;
    }
}

/// <summary>Helpers over many groups, as offered in the window.</summary>
public static class DuplicateMarking
{
    /// <returns>Number of groups that changed.</returns>
    public static int MarkAllExceptNewest(IEnumerable<DuplicateGroup> groups)
        => groups.Count(g => g.MarkAllExceptNewest());

    /// <returns>Number of groups that changed.</returns>
    public static int MarkAllExceptIn(IEnumerable<DuplicateGroup> groups, string folder)
        => groups.Count(g => g.MarkAllExceptIn(folder));

    public static void ClearMarks(IEnumerable<DuplicateGroup> groups)
    {
        foreach (var g in groups)
        {
            g.ClearMarks();
        }
    }

    public static int MarkedCount(IEnumerable<DuplicateGroup> groups) => groups.Sum(g => g.MarkedCount);

    public static long MarkedBytes(IEnumerable<DuplicateGroup> groups) => groups.Sum(g => g.MarkedBytes);
}

/// <summary>What happened to one marked file in <see cref="DuplicateRecycler.Recycle"/>.</summary>
public enum DuplicateRecycleOutcome
{
    /// <summary>Moved to the Recycle Bin.</summary>
    Moved,

    /// <summary>Size or time changed since the comparison, or the file is gone: skipped.</summary>
    Changed,

    /// <summary>No unmarked file of the group is still there unchanged: nothing of the group was moved.</summary>
    KeptCopyChanged,

    /// <summary>Not allowed (share, removable drive, unchecked group, or the group would lose its last file).</summary>
    NotAllowed,

    /// <summary>The move failed; the message says why.</summary>
    Failed,
}

public sealed record DuplicateRecycleItem(DuplicateFile File, DuplicateRecycleOutcome Outcome, string? Error = null);

/// <summary>
/// "Move to Recycle Bin" for duplicates (PLAN.md 2.9, 11.2). The caller has asked the user. Right before each move the
/// size and time of the file are checked again; a changed file is skipped. A group whose kept files are all gone or
/// changed is skipped as a whole, so the last copy is never moved. Only files on local fixed drives, only into the
/// Recycle Bin (<see cref="IRecycleBin"/>), never permanently. Moved files leave their group.
/// </summary>
public sealed class DuplicateRecycler(IRecycleBin bin, ILocalFileAccess files)
{
    /// <param name="moved">Called after each successful move (e.g. to update the directory); exceptions are not caught.</param>
    public IReadOnlyList<DuplicateRecycleItem> Recycle(
        IEnumerable<DuplicateGroup> groups, Action<DuplicateFile>? moved = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var report = new List<DuplicateRecycleItem>();
        foreach (var group in groups.ToList())
        {
            var marked = group.Files.Where(f => f.IsMarked).ToList();
            if (marked.Count == 0)
            {
                continue;
            }

            var kept = group.Files.Where(f => !f.IsMarked).ToList();
            if (!group.IsVerified || kept.Count == 0)
            {
                report.AddRange(marked.Select(f => new DuplicateRecycleItem(f, DuplicateRecycleOutcome.NotAllowed)));
                continue;
            }

            if (!kept.Any(Unchanged))
            {
                report.AddRange(marked.Select(f => new DuplicateRecycleItem(f, DuplicateRecycleOutcome.KeptCopyChanged)));
                continue;
            }

            foreach (var file in marked)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!file.CanRecycle)
                {
                    report.Add(new DuplicateRecycleItem(file, DuplicateRecycleOutcome.NotAllowed));
                    continue;
                }

                if (!Unchanged(file))
                {
                    report.Add(new DuplicateRecycleItem(file, DuplicateRecycleOutcome.Changed));
                    continue;
                }

                try
                {
                    bin.MoveFile(file.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
                                               or System.Security.SecurityException or InvalidOperationException)
                {
                    report.Add(new DuplicateRecycleItem(file, DuplicateRecycleOutcome.Failed, ex.Message));
                    continue;
                }

                file.IsMarked = false;
                group.Remove(file);
                report.Add(new DuplicateRecycleItem(file, DuplicateRecycleOutcome.Moved));
                moved?.Invoke(file);
            }
        }

        return report;
    }

    /// <summary>
    /// After a move: removes the entry from the directory if the file is really gone and its folder and location can still
    /// be seen (<see cref="ExistenceCheck"/>, the same rule as for a hit that cannot be found), and only if the entry still
    /// stands for this path. Returns true if it was removed.
    /// </summary>
    public static bool ForgetIfVanished(IndexStore store, DuplicateFile file, IPathProbe probe)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(probe);
        var roots = store.GetRoots().Select(r => r.Path).ToList();
        if (ExistenceCheck.Evaluate(file.FullPath, roots, probe) != ExistenceResult.Vanished)
        {
            return false;
        }

        var current = new DatabaseEntryResolver(store.Database).Resolve([file.EntryId], CancellationToken.None)[0];
        if (current is null || !string.Equals(current.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return store.DeleteSubtree(file.EntryId) > 0;
    }

    // Verified groups hold local files only, so probing never touches a share.
    private bool Unchanged(DuplicateFile file)
        => file.Group.IsVerified
           && files.Probe(file.FullPath) is { } now && now.Size == file.Size && now.LastWriteUtcTicks == file.MTimeTicks;
}
