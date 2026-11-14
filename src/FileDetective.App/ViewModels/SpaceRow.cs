using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>What the tree of the storage window needs from outside: the snapshot and the loader of a folder's files.</summary>
/// <param name="Snapshot">The numbers shown.</param>
/// <param name="LoadFiles">
/// Loads the files of one folder (on a background thread; for shares from the directory, never from the server). The
/// continuation runs on the UI thread because rows are created there.
/// </param>
internal sealed record SpaceTreeContext(SpaceSnapshot Snapshot, Func<SpaceFolder, Task<FolderFileListing>> LoadFiles);

internal enum SpaceRowKind
{
    Folder,
    Unreadable,
    File,
    Loading,
    FilesFailed,
    MoreFiles,

    /// <summary>A folder that could not be listed, below the line "Unreadable or system area"; size unknown.</summary>
    Locked,

    /// <summary>"n more" below the locked folders when there are more than <see cref="LockedFolderList.DefaultLimit"/>.</summary>
    MoreLocked,
}

/// <summary>
/// One line of the tree in the storage window: a folder (with size, share, counts and date), a file of an expanded
/// folder, or an extra line ("Unreadable or system area", "Loading…", a failed listing, "n more files"). Subfolders are
/// created when the line is first needed; the files of a folder are loaded when it is expanded (PLAN.md 11.1).
/// </summary>
internal sealed class SpaceRow : ObservableObject
{
    private readonly SpaceTreeContext _context;
    private readonly string _text = string.Empty;
    private readonly long _extraCount;
    private readonly long _extraSize;
    private ObservableCollection<SpaceRow>? _children;
    private List<SpaceRow>? _folderRows;
    private List<SpaceRow> _fileRows = [];
    private List<SpaceRow>? _lockedRows;
    private SpaceRow? _unreadableRow;
    private SpaceRow? _statusRow;
    private SpaceRow? _moreRow;
    private bool _isExpanded;
    private bool _filesRequested;
    private bool _filesLoaded;

    private SpaceRow(
        SpaceTreeContext context, SpaceRowKind kind, SpaceRow? parent, SpaceFolder? folder = null, SpaceFile? file = null,
        string text = "", long extraCount = 0, long extraSize = 0)
    {
        _context = context;
        Kind = kind;
        Parent = parent;
        Folder = folder;
        File = file;
        _text = text;
        _extraCount = extraCount;
        _extraSize = extraSize;
    }

    public SpaceRowKind Kind { get; }

    /// <summary>The folder; null for every line that is not a folder.</summary>
    public SpaceFolder? Folder { get; }

    /// <summary>The file; null for every line that is not a file.</summary>
    public SpaceFile? File { get; }

    public SpaceRow? Parent { get; }

    /// <summary>The path of a locked folder line (it has Show in Explorer and Copy path only); null for every other line.</summary>
    public string? LockedPath => Kind == SpaceRowKind.Locked ? _text : null;

    /// <summary>Lines that are no folder or file (shown in italics, no actions).</summary>
    public bool IsMutedRow => Kind is SpaceRowKind.Unreadable or SpaceRowKind.Loading or SpaceRowKind.FilesFailed or SpaceRowKind.MoreFiles
        or SpaceRowKind.MoreLocked;

    public bool IsRoot => Parent is null;

    /// <summary>Left margin of the line: 18 units per level below the top line.</summary>
    public Thickness Indent
    {
        get
        {
            int depth = 0;
            for (var p = Parent; p is not null; p = p.Parent)
            {
                depth++;
            }

            return new Thickness(depth * 18, 0, 0, 0);
        }
    }

    /// <summary>Expanded state; kept in the line so it survives the recycling of the tree's containers. Expanding loads the files.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value)
            {
                StartLoadingFiles();
            }
        }
    }

    public static SpaceRow CreateRoot(SpaceTreeContext context)
    {
        var root = new SpaceRow(context, SpaceRowKind.Folder, null, context.Snapshot.Root);
        root.IsExpanded = true; // only the top line starts expanded
        return root;
    }

    public ObservableCollection<SpaceRow> Children => _children ??= BuildChildren();

    public string Name => Kind switch
    {
        SpaceRowKind.Folder => Folder!.IsRecycleBin ? Strings.Get("Storage_RecycleBinName") : Folder.Name,
        SpaceRowKind.File => File!.Name,
        SpaceRowKind.Unreadable => Strings.Get("Storage_Unreadable"),
        SpaceRowKind.Loading => Strings.Get("Storage_FilesLoading"),
        SpaceRowKind.FilesFailed => _text,
        SpaceRowKind.Locked => _text,
        SpaceRowKind.MoreLocked => LocationUi.Format("Storage_LockedMore", _extraCount),
        _ => LocationUi.Format("Storage_FilesMore", _extraCount, ByteSize.Format(_extraSize)),
    };

    public string ToolTip => Kind switch
    {
        SpaceRowKind.Folder => Folder!.FullPath,
        SpaceRowKind.File => File!.FullPath,
        SpaceRowKind.Unreadable => Strings.Get("Storage_UnreadableTip"),
        SpaceRowKind.FilesFailed => _text,
        SpaceRowKind.Locked => _text,
        _ => string.Empty,
    };

    /// <summary>Hint text under the name: unreadable folder, link, cloud-only files.</summary>
    public string Note
    {
        get
        {
            if (Kind == SpaceRowKind.File)
            {
                return File!.IsCloudOnly ? LocationUi.Format("Storage_NoteCloudFile", ByteSize.Format(File.CloudSize)) : string.Empty;
            }

            if (Kind == SpaceRowKind.Locked)
            {
                return Strings.Get("Storage_NoteLocked");
            }

            if (Kind == SpaceRowKind.Unreadable)
            {
                return _context.Snapshot.FailedDirectories > 0
                    ? LocationUi.Format("Storage_NoteUnreadableCount", _context.Snapshot.FailedDirectories)
                    : string.Empty;
            }

            if (Folder is null)
            {
                return string.Empty;
            }

            var parts = new List<string>(3);
            if (Folder.IsLink)
            {
                parts.Add(Strings.Get("Storage_NoteLink"));
            }

            if (Folder.IsUnreadable)
            {
                parts.Add(Strings.Get("Storage_NoteUnreadable"));
            }

            if (Folder.CloudFileCount > 0)
            {
                parts.Add(LocationUi.Format("Storage_NoteCloud", Folder.CloudFileCount, ByteSize.Format(Folder.CloudSize)));
            }

            return string.Join(" · ", parts);
        }
    }

    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Loading…", a failed listing and locked folders (size unknown) have no numbers and no bar.</summary>
    public Visibility BarVisibility => Kind is SpaceRowKind.Loading or SpaceRowKind.FilesFailed or SpaceRowKind.Locked or SpaceRowKind.MoreLocked
        ? Visibility.Collapsed
        : Visibility.Visible;

    /// <summary>Bytes the line stands for. The top line includes the unreadable part, so the shares below add up to 100 %.</summary>
    private long DisplaySize => Kind switch
    {
        SpaceRowKind.Folder => IsRoot ? Folder!.Size + (_context.Snapshot.UnreadableBytes ?? 0) : Folder!.Size,
        SpaceRowKind.Unreadable => _context.Snapshot.UnreadableBytes ?? 0,
        SpaceRowKind.File => File!.Size,
        SpaceRowKind.MoreFiles => _extraSize,
        _ => 0,
    };

    public string SizeText => BarVisibility == Visibility.Visible ? ByteSize.Format(DisplaySize) : string.Empty;

    /// <summary>Share of the line in its parent folder (the top line: 1).</summary>
    public double Fraction
    {
        get
        {
            if (Parent is null)
            {
                return 1;
            }

            long total = Parent.DisplaySize;
            return total <= 0 ? 0 : Math.Min(1, (double)DisplaySize / total);
        }
    }

    public string PercentText => BarVisibility == Visibility.Visible ? Fraction.ToString("P0", CultureInfo.CurrentCulture) : string.Empty;

    public string FilesText => Folder is null ? string.Empty : Folder.FileCount.ToString("N0", CultureInfo.CurrentCulture);

    public string FoldersText => Folder is null ? string.Empty : Folder.FolderCount.ToString("N0", CultureInfo.CurrentCulture);

    public string DateText => (Folder?.LastWriteUtc ?? File?.LastWriteUtc) is { } utc
        ? utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>Recomputes everything shown (after something below was moved to the Recycle Bin).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>
    /// Takes a line out of the tree and refreshes everything whose numbers changed: the lines above it and the shares of
    /// all lines beside them. Call after the folder or file was removed from the snapshot.
    /// </summary>
    public void RemoveFromTree()
    {
        if (Parent is null)
        {
            return;
        }

        Parent._folderRows?.Remove(this);
        Parent._fileRows.Remove(this);
        Parent._children?.Remove(this);
        RefreshAbove();
    }

    /// <summary>
    /// Replaces this folder line by a line for <paramref name="newFolder"/> (after the folder was read again, e.g. the
    /// emptied Recycle Bin) and refreshes everything whose numbers changed.
    /// </summary>
    public void ReplaceFolderInTree(SpaceFolder newFolder)
    {
        if (Parent is not { } parent || parent._folderRows is null)
        {
            return;
        }

        int index = parent._folderRows.IndexOf(this);
        if (index < 0)
        {
            return;
        }

        parent._folderRows[index] = new SpaceRow(_context, SpaceRowKind.Folder, parent, newFolder);
        parent.Arrange();
        RefreshAbove();
    }

    /// <summary>Removes the line of a file that was moved to the Recycle Bin from another list, if its folder was expanded.</summary>
    public void RemoveRowsOf(SpaceFile file)
    {
        var chain = new Stack<SpaceFolder>();
        for (var f = file.Parent; f is not null; f = f.Parent)
        {
            chain.Push(f);
        }

        if (chain.Count == 0 || !ReferenceEquals(chain.Pop(), Folder))
        {
            return;
        }

        var row = this;
        while (chain.Count > 0)
        {
            var next = chain.Pop();
            row = row._folderRows?.FirstOrDefault(r => ReferenceEquals(r.Folder, next));
            if (row is null)
            {
                return;
            }
        }

        row._fileRows.FirstOrDefault(r => r.File == file)?.RemoveFromTree();
    }

    /// <summary>Refreshes this line and every line below it that has been created.</summary>
    public void RefreshAll()
    {
        Refresh();
        if (_children is null)
        {
            return;
        }

        foreach (var child in _children)
        {
            child.RefreshAll();
        }
    }

    private void RefreshAbove()
    {
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.Refresh();
            ancestor.RefreshChildren();
        }
    }

    private void RefreshChildren()
    {
        if (_children is null)
        {
            return;
        }

        foreach (var child in _children)
        {
            child.Refresh();
        }
    }

    /// <summary>The folder has files of its own that are not listed yet (never for links).</summary>
    private bool HasFilesToLoad
        => Kind == SpaceRowKind.Folder && Folder is { IsLink: false } f && f.DirectFileCount + f.DirectCloudFileCount > 0;

    private ObservableCollection<SpaceRow> BuildChildren()
    {
        if (Kind == SpaceRowKind.Unreadable)
        {
            // PLAN.md 11.1: the folders that could not be listed, sorted by path, at most 200 plus "n more".
            var locked = _context.Snapshot.GetLockedFolders();
            _lockedRows = locked.Paths.Select(p => new SpaceRow(_context, SpaceRowKind.Locked, this, text: p)).ToList();
            if (locked.OmittedCount > 0)
            {
                _lockedRows.Add(new SpaceRow(_context, SpaceRowKind.MoreLocked, this, extraCount: locked.OmittedCount));
            }

            return new ObservableCollection<SpaceRow>(_lockedRows);
        }

        _folderRows = Kind == SpaceRowKind.Folder
            ? Folder!.Children.Select(c => new SpaceRow(_context, SpaceRowKind.Folder, this, c)).ToList()
            : [];
        if (Kind == SpaceRowKind.Folder && IsRoot && _context.Snapshot.UnreadableBytes is not null)
        {
            _unreadableRow = new SpaceRow(_context, SpaceRowKind.Unreadable, this);
        }

        // A folder with files gets a "Loading…" line at once: it is the reason the line can be expanded.
        if (HasFilesToLoad && !_filesLoaded)
        {
            _statusRow = new SpaceRow(_context, SpaceRowKind.Loading, this);
        }

        return new ObservableCollection<SpaceRow>(Arranged());
    }

    private List<SpaceRow> Arranged()
    {
        var rows = new List<SpaceRow>();
        if (_folderRows is not null)
        {
            rows.AddRange(_folderRows);
        }

        rows.AddRange(_fileRows);
        if (_unreadableRow is not null)
        {
            rows.Add(_unreadableRow);
        }

        // Folders and files together, largest first; the stable sort keeps folders before files of equal size.
        var sorted = rows.OrderByDescending(r => r.DisplaySize).ToList();
        if (_statusRow is not null)
        {
            sorted.Insert(0, _statusRow);
        }

        if (_moreRow is not null)
        {
            sorted.Add(_moreRow);
        }

        return sorted;
    }

    private void Arrange()
    {
        if (_children is null)
        {
            return;
        }

        _children.Clear();
        foreach (var row in Arranged())
        {
            _children.Add(row);
        }
    }

    private void StartLoadingFiles()
    {
        if (_filesRequested || !HasFilesToLoad)
        {
            return;
        }

        _filesRequested = true;
        _ = LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
    {
        _ = Children; // creates the "Loading…" line
        FolderFileListing? listing = null;
        string? error = null;
        try
        {
            listing = await _context.LoadFiles(Folder!);
        }
        catch (OperationCanceledException)
        {
            return; // the window is closing
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _filesLoaded = true;
        _fileRows = [];
        _moreRow = null;
        _statusRow = null;
        if (listing is not null)
        {
            _fileRows = listing.Files.Select(f => new SpaceRow(_context, SpaceRowKind.File, this, file: f)).ToList();
            if (listing.OmittedCount > 0)
            {
                _moreRow = new SpaceRow(_context, SpaceRowKind.MoreFiles, this, extraCount: listing.OmittedCount, extraSize: listing.OmittedSize);
            }
        }
        else
        {
            _statusRow = new SpaceRow(_context, SpaceRowKind.FilesFailed, this, text: LocationUi.Format("Storage_FilesFailed", error ?? string.Empty));
        }

        Arrange();
    }
}

/// <summary>One line of the list of the largest files.</summary>
internal sealed class SpaceFileRow(SpaceFile file)
{
    public SpaceFile File { get; } = file;

    public string Name => File.Name;

    public string FolderPath => File.Parent.FullPath;

    public string SizeText => ByteSize.Format(File.Size);

    public string DateText => File.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>One line of the list of drives and shares at the top of the storage window.</summary>
internal sealed partial class StorageDriveItem : ObservableObject
{
    public StorageDriveItem(string path, string title, RootKind kind, byte? rootId)
    {
        Path = path;
        Title = title;
        Kind = kind;
        RootId = rootId;
        KindText = LocationUi.KindText(kind);
    }

    /// <summary>Drive root (<c>C:\</c>) or the path of the share location.</summary>
    public string Path { get; }

    public string Title { get; }

    public RootKind Kind { get; }

    /// <summary>The location number of a share; null for local drives.</summary>
    public byte? RootId { get; }

    public bool IsShare => Kind == RootKind.Network;

    public string KindText { get; }

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private string _spaceText = string.Empty;

    [ObservableProperty]
    private double _usedFraction;

    [ObservableProperty]
    private Visibility _barVisibility = Visibility.Collapsed;
}
