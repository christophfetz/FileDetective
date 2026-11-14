namespace FileDetective.Core;

/// <summary>Where the numbers of a <see cref="SpaceSnapshot"/> come from (PLAN.md 11.1).</summary>
public enum SpaceSource
{
    /// <summary>A complete walk of a local drive done for this analysis; kept in memory only.</summary>
    LiveScan,

    /// <summary>The existing directory (database), state of the last scan of a share. No access to the server.</summary>
    Directory,
}

/// <summary>
/// A folder in the storage analysis with its totals. <see cref="Size"/>, <see cref="FileCount"/> and
/// <see cref="FolderCount"/> include everything below the folder. Files that only exist in the cloud are counted in
/// <see cref="FileCount"/> and <see cref="CloudFileCount"/> but never in <see cref="Size"/>.
/// </summary>
public sealed class SpaceFolder
{
    private readonly List<SpaceFolder> _children = [];

    internal SpaceFolder()
    {
    }

    /// <summary>Name of the folder; for the top folder of an analysis its whole path.</summary>
    public string Name { get; internal set; } = string.Empty;

    public SpaceFolder? Parent { get; internal set; }

    /// <summary>Last write time of the folder itself, null if unknown (e.g. the root entry of a location).</summary>
    public DateTime? LastWriteUtc { get; internal set; }

    public FileAttributes Attributes { get; internal set; }

    /// <summary>A link (symbolic link, junction): recorded, never entered, its content is not counted.</summary>
    public bool IsLink { get; internal set; }

    /// <summary>The folder could not be listed; what is below it is missing from the numbers.</summary>
    public bool IsUnreadable { get; internal set; }

    /// <summary>
    /// The folder <c>$Recycle.Bin</c> directly in the root of a local drive (set for live walks only). It is shown as
    /// "Recycle Bin"; it is emptied (by Windows) instead of being moved to the Recycle Bin.
    /// </summary>
    public bool IsRecycleBin { get; internal set; }

    /// <summary>Number of the entry in the index; set for folders read from the directory (shares), null for live walks.</summary>
    public int? EntryId { get; internal set; }

    /// <summary>Name of the Recycle Bin folder in a drive root.</summary>
    public const string RecycleBinName = "$Recycle.Bin";

    /// <summary>Name of the system folder in a drive root that is never offered for the Recycle Bin.</summary>
    public const string SystemVolumeInformationName = "System Volume Information";

    /// <summary>Subfolders, largest first.</summary>
    public IReadOnlyList<SpaceFolder> Children => _children;

    /// <summary>Bytes of the files in this folder itself (without cloud-only files).</summary>
    public long DirectSize { get; internal set; }

    public long DirectFileCount { get; internal set; }

    /// <summary>Total size of everything below, without cloud-only files.</summary>
    public long Size { get; internal set; }

    /// <summary>Number of files below, cloud-only ones included.</summary>
    public long FileCount { get; internal set; }

    /// <summary>Number of folders below (not counting this one).</summary>
    public long FolderCount { get; internal set; }

    /// <summary>Number of cloud-only files below (attribute RecallOnDataAccess or Offline); they are never opened.</summary>
    public long CloudFileCount { get; internal set; }

    /// <summary>Logical size of the cloud-only files below; informational, not part of <see cref="Size"/>.</summary>
    public long CloudSize { get; internal set; }

    public long DirectCloudFileCount { get; internal set; }

    public long DirectCloudSize { get; internal set; }

    /// <summary>Set while the totals are computed: the folder can be reached from the top folder.</summary>
    internal bool Reachable { get; set; }

    internal List<SpaceFolder> ChildList => _children;

    /// <summary>The full path (top folder name, then the names down to this folder).</summary>
    public string FullPath
    {
        get
        {
            var names = new Stack<string>();
            for (var f = this; f is not null; f = f.Parent)
            {
                names.Push(f.Name);
            }

            string path = names.Pop();
            while (names.Count > 0)
            {
                path = Path.Join(path, names.Pop());
            }

            return path;
        }
    }

    /// <summary>True if this folder is <paramref name="other"/> or lies below it.</summary>
    public bool IsSameOrBelow(SpaceFolder other)
    {
        for (var f = this; f is not null; f = f.Parent)
        {
            if (ReferenceEquals(f, other))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// A file of an analysis: one of the largest files of the whole analysis (no cloud-only files there, they occupy no
/// space), or a file shown below its folder in the tree (a cloud-only file has <see cref="Size"/> 0 and keeps its
/// logical size in <see cref="CloudSize"/>).
/// </summary>
public sealed record SpaceFile(
    SpaceFolder Parent, string Name, long Size, DateTime LastWriteUtc, FileAttributes Attributes, long CloudSize = 0)
{
    public string FullPath => Path.Join(Parent.FullPath, Name);

    /// <summary>The file only exists in the cloud; it counts with size 0.</summary>
    public bool IsCloudOnly => SpaceCollector.IsCloudOnly(Attributes);

    /// <summary>A file as shown in the tree: cloud-only files count with size 0.</summary>
    public static SpaceFile ForTree(SpaceFolder parent, string name, long size, DateTime lastWriteUtc, FileAttributes attributes)
    {
        size = Math.Max(0, size);
        return SpaceCollector.IsCloudOnly(attributes)
            ? new SpaceFile(parent, name, 0, lastWriteUtc, attributes, size)
            : new SpaceFile(parent, name, size, lastWriteUtc, attributes);
    }
}

/// <summary>
/// The files of one folder as shown in the tree after it was expanded (PLAN.md 11.1): the largest
/// <see cref="Limit"/> files, largest first, and the number and total size of the files that are left out.
/// </summary>
public sealed record FolderFileListing(IReadOnlyList<SpaceFile> Files, long OmittedCount, long OmittedSize)
{
    /// <summary>At most this many files are shown per folder.</summary>
    public const int Limit = 1_000;

    public static FolderFileListing Empty { get; } = new([], 0, 0);

    /// <summary>Sorts by size (largest first, then by name) and cuts the list at <see cref="Limit"/>.</summary>
    public static FolderFileListing Create(IEnumerable<SpaceFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var all = files.ToList();
        all.Sort(BySizeDescending);
        if (all.Count <= Limit)
        {
            return new FolderFileListing(all, 0, 0);
        }

        long omittedSize = 0;
        for (int i = Limit; i < all.Count; i++)
        {
            omittedSize += all[i].Size;
        }

        return new FolderFileListing(all.GetRange(0, Limit), all.Count - Limit, omittedSize);
    }

    internal static int BySizeDescending(SpaceFile a, SpaceFile b)
    {
        int bySize = b.Size.CompareTo(a.Size);
        return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The result of a storage analysis (PLAN.md 11.1): the folder tree with totals, the largest files and the facts about
/// the run. Lives in memory only; nothing of it is written to the index.
/// </summary>
public sealed class SpaceSnapshot
{
    private readonly List<SpaceFile> _topFiles;

    internal SpaceSnapshot(
        SpaceSource source,
        RootKind kind,
        SpaceFolder root,
        List<SpaceFile> topFiles,
        ScanOutcome outcome,
        int failedDirectories,
        int linksNotEntered,
        DateTimeOffset? asOf,
        DriveSpace? driveSpace,
        ScanStatus? directoryStatus)
    {
        Source = source;
        Kind = kind;
        Root = root;
        _topFiles = topFiles;
        Outcome = outcome;
        FailedDirectories = failedDirectories;
        LinksNotEntered = linksNotEntered;
        AsOf = asOf;
        DriveSpace = driveSpace;
        DirectoryStatus = directoryStatus;
    }

    public SpaceSource Source { get; }

    public RootKind Kind { get; }

    /// <summary>The top folder (drive or location); its <see cref="SpaceFolder.Name"/> is the whole path.</summary>
    public SpaceFolder Root { get; }

    /// <summary>The largest files, largest first, at most <see cref="SpaceCollector.TopFilesLimit"/>.</summary>
    public IReadOnlyList<SpaceFile> TopFiles => _topFiles;

    /// <summary>How the walk ended (live scan) or how the last scan of the share ended (directory).</summary>
    public ScanOutcome Outcome { get; }

    /// <summary>Folders that could not be listed.</summary>
    public int FailedDirectories { get; }

    public int LinksNotEntered { get; }

    /// <summary>Time of the numbers: end of the live walk, or the last complete scan of the share (null: never).</summary>
    public DateTimeOffset? AsOf { get; }

    /// <summary>Total and free bytes of the drive at the end of a live walk; null for shares and when unknown.</summary>
    public DriveSpace? DriveSpace { get; private set; }

    /// <summary>For <see cref="SpaceSource.Directory"/>: the scan state of the location in the database.</summary>
    public ScanStatus? DirectoryStatus { get; }

    /// <summary>Bytes of the files that were read (cloud-only files excluded).</summary>
    public long ReadBytes => Root.Size;

    /// <summary>
    /// The difference between the space the drive reports as used and the sum of the files that were read: what could
    /// not be read (no administrator rights) plus system areas (page file, metadata, shadow copies). Null without drive
    /// information (shares), never negative.
    /// </summary>
    public long? UnreadableBytes => DriveSpace is { } space ? Math.Max(0, space.UsedBytes - ReadBytes - RecycledBytes) : null;

    /// <summary>
    /// Bytes that were moved to the Recycle Bin since the walk. They were taken out of the numbers but still occupy the
    /// drive until the bin is emptied, so they must not show up as "unreadable".
    /// </summary>
    public long RecycledBytes { get; private set; }

    /// <summary>True for numbers from a live walk of a local drive.</summary>
    public bool IsLocal => Source == SpaceSource.LiveScan;

    /// <summary>
    /// "Move to Recycle Bin" is only offered for entries of a local fixed drive (PLAN.md 2.9, 11.1): never for shares,
    /// and not for removable drives (they have no Recycle Bin, a delete would be final).
    /// </summary>
    public bool CanMoveToRecycleBin => Source == SpaceSource.LiveScan && Kind == RootKind.Local;

    /// <summary>
    /// Takes a folder out of the numbers after it was moved to the Recycle Bin: totals of all folders above are reduced,
    /// the largest files below it are dropped.
    /// </summary>
    public void RemoveFolder(SpaceFolder folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (folder.Parent is not { } parent)
        {
            throw new InvalidOperationException("The top folder cannot be removed from the analysis.");
        }

        parent.ChildList.Remove(folder);
        for (var f = parent; f is not null; f = f.Parent)
        {
            f.Size -= folder.Size;
            f.FileCount -= folder.FileCount;
            f.FolderCount -= folder.FolderCount + 1;
            f.CloudFileCount -= folder.CloudFileCount;
            f.CloudSize -= folder.CloudSize;
            f.ChildList.Sort(SpaceCollector.BySizeDescending);
        }

        _topFiles.RemoveAll(file => file.Parent.IsSameOrBelow(folder));
        RecycledBytes += folder.Size;
    }

    /// <summary>
    /// Replaces a folder by the result of a fresh walk of the same path (used after the Recycle Bin was emptied): the
    /// totals of all folders above and the list of the largest files are corrected. Returns false and changes nothing if
    /// the fresh walk did not complete (the folder could not be listed).
    /// </summary>
    public bool ReplaceFolder(SpaceFolder old, SpaceSnapshot fresh)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(fresh);
        if (old.Parent is not { } parent)
        {
            throw new InvalidOperationException("The top folder cannot be replaced.");
        }

        if (fresh.Outcome is not (ScanOutcome.Complete or ScanOutcome.CompleteWithErrors))
        {
            return false;
        }

        int index = parent.ChildList.IndexOf(old);
        if (index < 0)
        {
            return false;
        }

        var neu = fresh.Root;
        neu.Name = old.Name;
        neu.Parent = parent;
        neu.LastWriteUtc = old.LastWriteUtc;
        neu.Attributes = old.Attributes;
        neu.IsRecycleBin = old.IsRecycleBin;
        parent.ChildList[index] = neu;
        for (var f = parent; f is not null; f = f.Parent)
        {
            f.Size += neu.Size - old.Size;
            f.FileCount += neu.FileCount - old.FileCount;
            f.FolderCount += neu.FolderCount - old.FolderCount;
            f.CloudFileCount += neu.CloudFileCount - old.CloudFileCount;
            f.CloudSize += neu.CloudSize - old.CloudSize;
            f.ChildList.Sort(SpaceCollector.BySizeDescending);
        }

        _topFiles.RemoveAll(file => file.Parent.IsSameOrBelow(old));
        _topFiles.AddRange(fresh._topFiles);
        _topFiles.Sort((a, b) =>
        {
            int bySize = b.Size.CompareTo(a.Size);
            return bySize != 0 ? bySize : string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
        });
        if (_topFiles.Count > SpaceCollector.TopFilesLimit)
        {
            _topFiles.RemoveRange(SpaceCollector.TopFilesLimit, _topFiles.Count - SpaceCollector.TopFilesLimit);
        }

        return true;
    }

    /// <summary>
    /// After the Recycle Bin of the drive was emptied: what was moved there since the walk is gone from the drive too,
    /// so it no longer has to be kept apart; <paramref name="space"/> is the fresh space of the drive (null: unknown).
    /// </summary>
    public void RecycleBinWasEmptied(DriveSpace? space)
    {
        RecycledBytes = 0;
        if (space is not null)
        {
            DriveSpace = space;
        }
    }

    /// <summary>Takes one file out of the numbers after it was moved to the Recycle Bin.</summary>
    public void RemoveFile(SpaceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.IsCloudOnly)
        {
            // Occupies no space: only the counts change. A tree row can be a different object than the one in the list.
            file.Parent.DirectCloudFileCount--;
            file.Parent.DirectCloudSize -= file.CloudSize;
            for (var f = file.Parent; f is not null; f = f.Parent)
            {
                f.FileCount--;
                f.CloudFileCount--;
                f.CloudSize -= file.CloudSize;
            }

            return;
        }

        _topFiles.Remove(file);
        file.Parent.DirectSize -= file.Size;
        file.Parent.DirectFileCount--;
        RecycledBytes += file.Size;
        for (var f = file.Parent; f is not null; f = f.Parent)
        {
            f.Size -= file.Size;
            f.FileCount--;
            f.ChildList.Sort(SpaceCollector.BySizeDescending);
        }
    }
}

/// <summary>
/// Collects what an analysis finds (folders, files) and builds the <see cref="SpaceSnapshot"/>. Used by the live walk
/// and by the reader of the directory, so both apply the same rules: cloud-only files are counted apart and never
/// add to the size, and the largest files are kept in a bounded list. Not thread-safe.
/// </summary>
public sealed class SpaceCollector
{
    /// <summary>The list of the largest files holds at most this many entries (PLAN.md 11.1).</summary>
    public const int TopFilesLimit = 1_000;

    /// <summary>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS: the content is in the cloud and is fetched on access.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;

    private readonly List<SpaceFolder> _folders = [];

    // Smallest file first, so the weakest one is dropped when the list is full; at equal size the later one goes first.
    private readonly PriorityQueue<SpaceFile, (long Size, long NegativeOrder)> _top = new();
    private long _order;
    private int _failed;
    private int _links;

    internal static int BySizeDescending(SpaceFolder a, SpaceFolder b)
    {
        int bySize = b.Size.CompareTo(a.Size);
        return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for a file that only exists in the cloud (attribute RecallOnDataAccess or Offline). Never opened.</summary>
    public static bool IsCloudOnly(FileAttributes attributes)
        => (attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0;

    /// <summary>Creates a folder that is described later with <see cref="Describe"/> (the directory reader meets children before parents).</summary>
    public SpaceFolder NewFolder()
    {
        var folder = new SpaceFolder();
        _folders.Add(folder);
        return folder;
    }

    /// <summary>Sets name, parent and facts of a folder. <paramref name="parent"/> is null for the top folder.</summary>
    public void Describe(SpaceFolder folder, SpaceFolder? parent, string name, DateTime? lastWriteUtc, FileAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(name);
        folder.Name = name;
        folder.Parent = parent;
        folder.LastWriteUtc = lastWriteUtc;
        folder.Attributes = attributes;
    }

    public SpaceFolder AddFolder(SpaceFolder? parent, string name, DateTime? lastWriteUtc, FileAttributes attributes)
    {
        var folder = NewFolder();
        Describe(folder, parent, name, lastWriteUtc, attributes);
        return folder;
    }

    /// <summary>Adds a file to its folder. A cloud-only file is only counted apart, it adds no size.</summary>
    public void AddFile(SpaceFolder parent, string name, long size, DateTime lastWriteUtc, FileAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (size < 0)
        {
            size = 0;
        }

        if (IsCloudOnly(attributes))
        {
            parent.DirectCloudFileCount++;
            parent.DirectCloudSize += size;
            return;
        }

        parent.DirectFileCount++;
        parent.DirectSize += size;
        var file = new SpaceFile(parent, name, size, lastWriteUtc, attributes);
        _top.Enqueue(file, (size, -_order++));
        if (_top.Count > TopFilesLimit)
        {
            _top.Dequeue();
        }
    }

    /// <summary>The folder could not be listed.</summary>
    public void MarkUnreadable(SpaceFolder folder)
    {
        folder.IsUnreadable = true;
        _failed++;
    }

    /// <summary>The folder is a link and is not entered.</summary>
    public void MarkLink(SpaceFolder folder)
    {
        folder.IsLink = true;
        _links++;
    }

    /// <summary>
    /// Computes the totals from the top folder down (own stack, no recursion) and returns the snapshot. Folders that
    /// cannot be reached from <paramref name="root"/> (orphans in a damaged directory) and their files are left out.
    /// </summary>
    public SpaceSnapshot Build(
        SpaceFolder root,
        SpaceSource source,
        RootKind kind,
        ScanOutcome outcome,
        DateTimeOffset? asOf,
        DriveSpace? driveSpace = null,
        ScanStatus? directoryStatus = null,
        int? failedDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        foreach (var folder in _folders)
        {
            folder.ChildList.Clear();
            folder.Reachable = false;
        }

        foreach (var folder in _folders)
        {
            if (folder.Parent is { } parent && !ReferenceEquals(folder, root))
            {
                parent.ChildList.Add(folder);
            }
        }

        if (source == SpaceSource.LiveScan)
        {
            foreach (var child in root.ChildList)
            {
                child.IsRecycleBin = string.Equals(child.Name, SpaceFolder.RecycleBinName, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Pre-order walk from the root; a folder visited twice (cycle in a damaged directory) is ignored.
        var order = new List<SpaceFolder>(_folders.Count);
        var stack = new Stack<SpaceFolder>();
        root.Reachable = true;
        stack.Push(root);
        while (stack.Count > 0)
        {
            var folder = stack.Pop();
            order.Add(folder);
            foreach (var child in folder.ChildList)
            {
                if (!child.Reachable)
                {
                    child.Reachable = true;
                    stack.Push(child);
                }
            }
        }

        for (int i = order.Count - 1; i >= 0; i--)
        {
            var folder = order[i];
            long size = folder.DirectSize;
            long files = folder.DirectFileCount + folder.DirectCloudFileCount;
            long folders = 0;
            long cloudFiles = folder.DirectCloudFileCount;
            long cloudSize = folder.DirectCloudSize;
            foreach (var child in folder.ChildList)
            {
                size += child.Size;
                files += child.FileCount;
                folders += child.FolderCount + 1;
                cloudFiles += child.CloudFileCount;
                cloudSize += child.CloudSize;
            }

            folder.Size = size;
            folder.FileCount = files;
            folder.FolderCount = folders;
            folder.CloudFileCount = cloudFiles;
            folder.CloudSize = cloudSize;
            folder.ChildList.Sort(BySizeDescending);
        }

        var top = _top.UnorderedItems.Select(i => i.Element).Where(f => f.Parent.Reachable).ToList();
        top.Sort((a, b) =>
        {
            int bySize = b.Size.CompareTo(a.Size);
            return bySize != 0 ? bySize : string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase);
        });

        return new SpaceSnapshot(
            source, kind, root, top, outcome, failedDirectories ?? _failed, _links, asOf, driveSpace, directoryStatus);
    }
}
