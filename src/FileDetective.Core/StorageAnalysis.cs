using System.ComponentModel;

namespace FileDetective.Core;

/// <summary>Total and free bytes of a drive.</summary>
public readonly record struct DriveSpace(long TotalBytes, long FreeBytes)
{
    /// <summary>What the drive reports as occupied.</summary>
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>Source of the space of a drive; the real one asks Windows, tests use a stand-in. Local drives only.</summary>
public interface IDriveSpaceSource
{
    /// <summary>Total and free bytes of the drive, or null if it is not ready or the numbers are unknown.</summary>
    DriveSpace? GetSpace(string driveRoot);
}

/// <summary>Real <see cref="IDriveSpaceSource"/> on top of <see cref="DriveInfo"/> (reads the volume, never a share).</summary>
public sealed class SystemDriveSpaceSource : IDriveSpaceSource
{
    public static SystemDriveSpaceSource Instance { get; } = new();

    public DriveSpace? GetSpace(string driveRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveRoot);
        try
        {
            var info = new DriveInfo(driveRoot);
            return info.IsReady ? new DriveSpace(info.TotalSize, info.TotalFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>Progress of a live walk: folders listed, files and bytes counted so far.</summary>
public readonly record struct SpaceProgress(long Directories, long Files, long Bytes);

/// <summary>
/// The complete walk of a local drive for the storage analysis (PLAN.md 11.1): no exclusion rules, everything the user
/// may read, into a <see cref="SpaceSnapshot"/> in memory. It lists directories through <see cref="IDirectoryLister"/>
/// and recognises links through <see cref="ILinkDetector"/> like the <see cref="Scanner"/> does, classifies listing
/// failures with the same rule, and has no connection to the index: it cannot write to it. Files are never opened.
/// A folder that cannot be listed is counted and the walk goes on. Single-threaded.
/// </summary>
public sealed class LocalSpaceScanner
{
    private readonly IDirectoryLister _lister;
    private readonly ILinkDetector _linkDetector;
    private readonly IDriveSpaceSource _space;
    private readonly TimeProvider _time;

    public LocalSpaceScanner(
        IDirectoryLister lister, ILinkDetector linkDetector, IDriveSpaceSource space, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(lister);
        ArgumentNullException.ThrowIfNull(linkDetector);
        ArgumentNullException.ThrowIfNull(space);
        _lister = lister;
        _linkDetector = linkDetector;
        _space = space;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Walks <paramref name="rootPath"/> completely. Cancelling returns a snapshot with <see cref="ScanOutcome.Cancelled"/>
    /// (the numbers are partial; the caller drops it). If the top folder cannot be listed the outcome is
    /// <see cref="ScanOutcome.RootUnreachable"/>.
    /// </summary>
    /// <param name="progress">Called on the walking thread after every folder; must be cheap and must not throw.</param>
    public SpaceSnapshot Scan(
        string rootPath, RootKind kind, Action<SpaceProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);

        // "C:" alone would mean the current directory of drive C.
        string start = rootPath.Length == 2 && rootPath[1] == ':' ? rootPath + "\\" : rootPath;
        var collector = new SpaceCollector();
        var root = collector.AddFolder(null, start, null, FileAttributes.Directory);
        var stack = new Stack<(SpaceFolder Folder, string Path)>();
        stack.Push((root, start));
        long directories = 0;
        long files = 0;
        long bytes = 0;
        bool rootChecked = false;
        var outcome = ScanOutcome.Complete;
        int failed = 0;

        try
        {
            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (folder, path) = stack.Pop();
                IReadOnlyList<RawEntry> listing;
                try
                {
                    listing = _lister.List(path);
                }
                catch (Exception ex) when (Scanner.IsListingFailure(ex))
                {
                    // The same rule as in the directory scan: count it, go on with the next folder.
                    collector.MarkUnreadable(folder);
                    failed++;
                    if (!rootChecked)
                    {
                        outcome = ScanOutcome.RootUnreachable;
                    }

                    rootChecked = true;
                    continue;
                }

                rootChecked = true;
                directories++;
                foreach (var e in listing)
                {
                    if (e.IsDirectory)
                    {
                        var child = collector.AddFolder(folder, e.Name, e.LastWriteUtc, e.Attributes);
                        string childPath = Path.Join(path, e.Name);
                        if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && _linkDetector.IsLink(childPath))
                        {
                            collector.MarkLink(child);
                            continue;
                        }

                        stack.Push((child, childPath));
                    }
                    else
                    {
                        collector.AddFile(folder, e.Name, e.Size, e.LastWriteUtc, e.Attributes);
                        files++;
                        if (!SpaceCollector.IsCloudOnly(e.Attributes))
                        {
                            bytes += Math.Max(0, e.Size);
                        }
                    }
                }

                progress?.Invoke(new SpaceProgress(directories, files, bytes));
            }

            if (outcome != ScanOutcome.RootUnreachable && failed > 0)
            {
                outcome = ScanOutcome.CompleteWithErrors;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = ScanOutcome.Cancelled;
        }

        DriveSpace? space = outcome is ScanOutcome.Complete or ScanOutcome.CompleteWithErrors
            ? _space.GetSpace(Path.GetPathRoot(start) ?? start)
            : null;
        return collector.Build(root, SpaceSource.LiveScan, kind, outcome, _time.GetUtcNow(), space);
    }
}

/// <summary>
/// Reads the numbers of a share from the existing directory (PLAN.md 11.1): the state of the last scan, with its date.
/// It has no <see cref="IDirectoryLister"/> and no other way to the server, so it cannot cause any load there.
/// </summary>
public sealed class DirectorySpaceReader
{
    private readonly IndexStore _store;

    public DirectorySpaceReader(IndexStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <exception cref="ArgumentException">The location does not exist.</exception>
    public SpaceSnapshot Read(byte rootId, CancellationToken cancellationToken = default)
    {
        var info = _store.GetRoot(rootId) ?? throw new ArgumentException($"Location {rootId} does not exist.", nameof(rootId));
        var collector = new SpaceCollector();
        var folders = new Dictionary<int, SpaceFolder>();

        SpaceFolder Get(int id)
        {
            if (!folders.TryGetValue(id, out var folder))
            {
                folders[id] = folder = collector.NewFolder();
                folder.EntryId = id;
            }

            return folder;
        }

        _store.ReadEntriesOfRoot(
            rootId,
            e =>
            {
                if (e.ParentId == 0)
                {
                    collector.Describe(Get(e.Id), null, e.Name, null, e.Attributes);
                }
                else if (e.IsDirectory)
                {
                    collector.Describe(Get(e.Id), Get(e.ParentId), e.Name, FromUnix(e.MTimeUnixSeconds), e.Attributes);
                }
                else
                {
                    collector.AddFile(Get(e.ParentId), e.Name, e.Size, FromUnix(e.MTimeUnixSeconds) ?? DateTime.UnixEpoch, e.Attributes);
                }
            },
            cancellationToken);

        var root = Get(info.RootEntryId);
        if (root.Name.Length == 0)
        {
            collector.Describe(root, null, info.Path, null, FileAttributes.Directory);
        }

        var outcome = info.LastScanStatus switch
        {
            ScanStatus.Complete => ScanOutcome.Complete,
            ScanStatus.CompleteWithErrors => ScanOutcome.CompleteWithErrors,
            _ => ScanOutcome.Cancelled,
        };
        DateTimeOffset? asOf = info.LastScanCompletedUtc is { } unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        return collector.Build(
            root, SpaceSource.Directory, info.Kind, outcome, asOf, driveSpace: null,
            directoryStatus: info.LastScanStatus, failedDirectories: info.LastScanFailedDirs);
    }

    private static DateTime? FromUnix(long seconds)
        => seconds <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
}

/// <summary>
/// Entry point of the storage analysis (PLAN.md 11.1). Shares are only ever read from the directory
/// (<see cref="ReadShare"/>), local drives are walked live (<see cref="ScanLocal"/>) and never a network drive: a path
/// that is a UNC path or a mapped network drive is refused before anything is listed.
/// </summary>
public sealed class StorageAnalysis
{
    private readonly Func<IndexStore?> _store;
    private readonly DriveDiscovery _drives;
    private readonly LocalSpaceScanner _scanner;
    private readonly IDirectoryLister _lister;
    private readonly IDriveSpaceSource _space;

    /// <param name="store">The index store, null while the directory is not loaded (shares cannot be read then).</param>
    public StorageAnalysis(
        Func<IndexStore?> store,
        IDirectoryLister lister,
        ILinkDetector linkDetector,
        DriveDiscovery drives,
        IDriveSpaceSource space,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(lister);
        _store = store;
        _drives = drives;
        _space = space;
        _lister = lister;
        _scanner = new LocalSpaceScanner(lister, linkDetector, space, time);
    }

    /// <summary>The numbers of a share from the directory; no access to the server.</summary>
    /// <exception cref="InvalidOperationException">The directory is not loaded, or the location is not a share.</exception>
    public SpaceSnapshot ReadShare(byte rootId, CancellationToken cancellationToken = default)
    {
        var store = _store() ?? throw new InvalidOperationException("The directory is not loaded.");
        if (store.GetRoot(rootId) is { Kind: not RootKind.Network })
        {
            throw new InvalidOperationException("Only shares are read from the directory; local drives are walked.");
        }

        return new DirectorySpaceReader(store).Read(rootId, cancellationToken);
    }

    /// <summary>A complete walk of a local or removable drive. Refuses network paths without touching them.</summary>
    /// <exception cref="InvalidOperationException">The path is on a network drive or not an absolute path.</exception>
    public SpaceSnapshot ScanLocal(
        string drivePath, Action<SpaceProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var kind = LocalKind(drivePath);
        return _scanner.Scan(drivePath, kind, progress, cancellationToken);
    }

    /// <summary>
    /// The files of one folder, for the tree (PLAN.md 11.1): the largest <see cref="FolderFileListing.Limit"/> and the sum of
    /// the rest. For a share they come from the directory (<see cref="IndexStore.GetKnownChildren"/>) and the
    /// <see cref="IDirectoryLister"/> is never asked, so there is no access to the server. For a local drive exactly this
    /// folder is listed (no file is opened); a listing failure is thrown to the caller.
    /// </summary>
    public FolderFileListing LoadFiles(SpaceSnapshot snapshot, SpaceFolder folder)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(folder);
        if (snapshot.Source == SpaceSource.Directory)
        {
            if (folder.EntryId is not { } id)
            {
                return FolderFileListing.Empty;
            }

            var store = _store() ?? throw new InvalidOperationException("The directory is not loaded.");
            return FolderFileListing.Create(
                store.GetKnownChildren(id)
                    .Where(k => !k.IsDirectory)
                    .Select(k => SpaceFile.ForTree(
                        folder, k.Name, k.Size, k.MTimeUnixSeconds <= 0 ? DateTime.UnixEpoch : DateTimeOffset.FromUnixTimeSeconds(k.MTimeUnixSeconds).UtcDateTime, k.Attributes)));
        }

        if (snapshot.Kind == RootKind.Network)
        {
            throw new InvalidOperationException("Shares are never listed; their files come from the directory.");
        }

        if (folder.IsLink)
        {
            return FolderFileListing.Empty; // links are never entered
        }

        return FolderFileListing.Create(
            _lister.List(folder.FullPath)
                .Where(e => !e.IsDirectory)
                .Select(e => SpaceFile.ForTree(folder, e.Name, e.Size, e.LastWriteUtc, e.Attributes)));
    }

    /// <summary>
    /// A fresh local walk of one folder of a live snapshot (the Recycle Bin after it was emptied); a local run of just that
    /// subtree. Apply it with <see cref="SpaceSnapshot.ReplaceFolder"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The snapshot is not from a local walk.</exception>
    public SpaceSnapshot ScanFolder(SpaceSnapshot snapshot, SpaceFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(folder);
        if (!snapshot.IsLocal || snapshot.Kind == RootKind.Network)
        {
            throw new InvalidOperationException("Only folders of a local walk are listed again.");
        }

        return _scanner.Scan(folder.FullPath, snapshot.Kind, null, cancellationToken);
    }

    /// <summary>Used and free space of a local or removable drive; null for network drives (never asked) and unknown drives.</summary>
    public DriveSpace? GetDriveSpace(string drivePath)
    {
        try
        {
            LocalKind(drivePath);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return _space.GetSpace(drivePath);
    }

    private RootKind LocalKind(string path)
    {
        var candidate = _drives.Describe(path)
            ?? throw new InvalidOperationException($"'{path}' is not an absolute drive path.");
        if (candidate.Kind == RootKind.Network)
        {
            throw new InvalidOperationException($"'{path}' is on a network drive; shares are only read from the directory.");
        }

        return candidate.Kind;
    }
}

/// <summary>Moves things to the Recycle Bin. The real one is <see cref="SystemRecycleBin"/>; tests use a stand-in.</summary>
public interface IRecycleBin
{
    void MoveFile(string path);

    void MoveDirectory(string path);
}

/// <summary>How an attempt to empty the Recycle Bin of a drive ended.</summary>
public enum RecycleBinEmptyResult
{
    /// <summary>Windows emptied the bin (or it was already empty).</summary>
    Emptied,

    /// <summary>The user said No in the dialog of Windows; nothing changed.</summary>
    Cancelled,
}

/// <summary>Empties the Recycle Bin of one drive. The real one is <see cref="SystemRecycleBin"/>; tests use a stand-in.</summary>
public interface IRecycleBinEmptier
{
    /// <summary>
    /// Asks Windows to empty the Recycle Bin of <paramref name="driveRoot"/> (for example <c>C:\</c>). Windows asks the
    /// user itself. <paramref name="ownerWindow"/> is the handle of the window that owns the dialog (0: none).
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows reported a failure.</exception>
    RecycleBinEmptyResult Empty(string driveRoot, nint ownerWindow);
}

/// <summary>
/// Real <see cref="IRecycleBin"/>: <c>Microsoft.VisualBasic.FileIO.FileSystem</c> with <c>RecycleOption.SendToRecycleBin</c>
/// (part of .NET, no package). Shows only error dialogs. As <see cref="IRecycleBinEmptier"/> it calls
/// <c>SHEmptyRecycleBin</c> of Windows for one drive, without <c>SHERB_NOCONFIRMATION</c>: Windows asks the user and
/// shows its own progress and sound.
/// </summary>
public sealed class SystemRecycleBin : IRecycleBin, IRecycleBinEmptier
{
    private const int SOk = 0;
    private const int SFalse = 1;
    private const int EAbort = unchecked((int)0x80004004);
    private const int ErrorCancelledAsHresult = unchecked((int)0x800704C7);
    private const int EUnexpected = unchecked((int)0x8000FFFF);

    public void MoveFile(string path)
        => Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);

    public void MoveDirectory(string path)
        => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
            path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);

    public RecycleBinEmptyResult Empty(string driveRoot, nint ownerWindow)
    {
        ArgumentException.ThrowIfNullOrEmpty(driveRoot);
        int hr = SHEmptyRecycleBin(ownerWindow, driveRoot, 0);
        switch (hr)
        {
            case SOk:
            case SFalse:
            // Some Windows versions report an already empty bin like this.
            case EUnexpected:
                return RecycleBinEmptyResult.Emptied;
            case EAbort:
            case ErrorCancelledAsHresult:
                return RecycleBinEmptyResult.Cancelled;
            default:
                throw new System.ComponentModel.Win32Exception(
                    hr & 0xFFFF,
                    $"The Recycle Bin could not be emptied (HRESULT 0x{hr:X8}).");
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int SHEmptyRecycleBin(nint hwnd, string? pszRootPath, uint dwFlags);
}

/// <summary>
/// "Move to Recycle Bin" and "Empty Recycle Bin" of the storage analysis (PLAN.md 2.9, 11.1). The caller asks the user
/// first (for the move; Windows asks itself when emptying); this class only refuses what must never happen: anything on a
/// share or a removable drive, the top folder, links, anything that is not part of the snapshot, and the places that
/// must never be touched: the Recycle Bin itself and everything in it, <c>System Volume Information</c> and everything in
/// it, the Windows folder and everything in it. It never deletes by itself and never permanently.
/// </summary>
/// <param name="bin">Moves to the Recycle Bin.</param>
/// <param name="windowsDirectory">The Windows folder (<c>Environment.SpecialFolder.Windows</c>); null: none is protected by path.</param>
/// <param name="emptier">Empties the Recycle Bin of a drive; null: the real <see cref="SystemRecycleBin"/>, or <paramref name="bin"/> if it can do so.</param>
public sealed class SpaceRecycler(IRecycleBin bin, string? windowsDirectory = null, IRecycleBinEmptier? emptier = null)
{
    private readonly string? _windows = string.IsNullOrWhiteSpace(windowsDirectory)
        ? null
        : Path.TrimEndingDirectorySeparator(Path.GetFullPath(windowsDirectory));

    private readonly IRecycleBinEmptier _emptier = emptier ?? bin as IRecycleBinEmptier ?? new SystemRecycleBin();

    public bool CanRecycle(SpaceSnapshot snapshot, SpaceFolder folder)
        => snapshot.CanMoveToRecycleBin && folder.Parent is not null && !folder.IsLink && folder.IsSameOrBelow(snapshot.Root)
            && !IsProtected(folder, null);

    public bool CanRecycle(SpaceSnapshot snapshot, SpaceFile file)
        => snapshot.CanMoveToRecycleBin && file.Parent.IsSameOrBelow(snapshot.Root) && !IsProtected(file.Parent, file.Name);

    /// <summary>
    /// The Recycle Bin of a local fixed drive can be emptied: <paramref name="folder"/> is the folder <c>$Recycle.Bin</c>
    /// in the drive root of a local walk. Never for shares and removable drives.
    /// </summary>
    public bool CanEmptyRecycleBin(SpaceSnapshot snapshot, SpaceFolder folder)
        => snapshot.CanMoveToRecycleBin && folder.IsRecycleBin && ReferenceEquals(folder.Parent, snapshot.Root);

    /// <summary>
    /// Asks Windows to empty the Recycle Bin of the drive of <paramref name="snapshot"/> (for this one drive); Windows asks
    /// the user. Does not touch the snapshot: call <see cref="StorageAnalysis.ScanFolder"/> and
    /// <see cref="SpaceSnapshot.ReplaceFolder"/> afterwards when the result is <see cref="RecycleBinEmptyResult.Emptied"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is not the Recycle Bin of a local fixed drive.</exception>
    public RecycleBinEmptyResult EmptyRecycleBin(SpaceSnapshot snapshot, SpaceFolder folder, nint ownerWindow = 0)
    {
        if (!CanEmptyRecycleBin(snapshot, folder))
        {
            throw new InvalidOperationException("This is not the Recycle Bin of a local drive.");
        }

        return _emptier.Empty(snapshot.Root.Name, ownerWindow);
    }

    /// <summary>
    /// True for what is never moved to the Recycle Bin: the Recycle Bin and anything in it, <c>System Volume
    /// Information</c> and anything in it, the Windows folder and anything in it.
    /// </summary>
    private bool IsProtected(SpaceFolder folder, string? fileName)
    {
        for (var f = folder; f is not null; f = f.Parent)
        {
            if (f.IsRecycleBin)
            {
                return true;
            }

            if (f.Parent is { Parent: null } && string.Equals(f.Name, SpaceFolder.SystemVolumeInformationName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (_windows is null)
        {
            return false;
        }

        string path = Path.TrimEndingDirectorySeparator(fileName is null ? folder.FullPath : Path.Join(folder.FullPath, fileName));
        return string.Equals(path, _windows, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(_windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Only the move: checks the rules and moves the folder to the Recycle Bin, without touching the snapshot. For callers
    /// that run the (possibly slow) move on a background thread and update the snapshot on the UI thread afterwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">The folder may not be moved (see <see cref="CanRecycle(SpaceSnapshot, SpaceFolder)"/>).</exception>
    public void MoveToBin(SpaceSnapshot snapshot, SpaceFolder folder)
    {
        if (!CanRecycle(snapshot, folder))
        {
            throw new InvalidOperationException("This folder cannot be moved to the Recycle Bin.");
        }

        bin.MoveDirectory(folder.FullPath);
    }

    /// <summary>Only the move, for a file; see <see cref="MoveToBin(SpaceSnapshot, SpaceFolder)"/>.</summary>
    public void MoveToBin(SpaceSnapshot snapshot, SpaceFile file)
    {
        if (!CanRecycle(snapshot, file))
        {
            throw new InvalidOperationException("This file cannot be moved to the Recycle Bin.");
        }

        bin.MoveFile(file.FullPath);
    }

    /// <summary>Moves the folder to the Recycle Bin and takes it out of the snapshot.</summary>
    public void Recycle(SpaceSnapshot snapshot, SpaceFolder folder)
    {
        MoveToBin(snapshot, folder);
        snapshot.RemoveFolder(folder);
    }

    /// <summary>Moves the file to the Recycle Bin and takes it out of the snapshot.</summary>
    public void Recycle(SpaceSnapshot snapshot, SpaceFile file)
    {
        MoveToBin(snapshot, file);
        snapshot.RemoveFile(file);
    }
}

/// <summary>Formats byte counts the way Windows shows them (1,024 based units).</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes, IFormatProvider? provider = null)
    {
        provider ??= System.Globalization.CultureInfo.CurrentCulture;
        if (bytes < 0)
        {
            bytes = 0;
        }

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string number = unit == 0
            ? value.ToString("N0", provider)
            : value.ToString(value >= 100 ? "N0" : value >= 10 ? "N1" : "N2", provider);
        return number + " " + Units[unit];
    }
}
