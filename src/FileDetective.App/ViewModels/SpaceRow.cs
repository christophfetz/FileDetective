using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>
/// One line of the folder tree in the storage window: a folder with size, share, counts and date, or the extra line
/// "Unreadable or system area". The children are created when the line is first expanded.
/// </summary>
internal sealed class SpaceRow : ObservableObject
{
    private readonly SpaceSnapshot _snapshot;
    private ObservableCollection<SpaceRow>? _children;

    private SpaceRow(SpaceSnapshot snapshot, SpaceFolder? folder, SpaceRow? parent)
    {
        _snapshot = snapshot;
        Folder = folder;
        Parent = parent;
    }

    /// <summary>The folder, or null for the line "Unreadable or system area".</summary>
    public SpaceFolder? Folder { get; }

    public SpaceRow? Parent { get; }

    public bool IsUnreadableRow => Folder is null;

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

    /// <summary>Only the top line starts expanded.</summary>
    public bool StartsExpanded => IsRoot;

    public static SpaceRow CreateRoot(SpaceSnapshot snapshot) => new(snapshot, snapshot.Root, null);

    public ObservableCollection<SpaceRow> Children => _children ??= BuildChildren();

    /// <summary>Whether the children have been created already (and so must be kept up to date).</summary>
    public bool ChildrenCreated => _children is not null;

    public string Name => Folder is null ? Strings.Get("Storage_Unreadable") : Folder.Name;

    public string ToolTip => Folder is null ? Strings.Get("Storage_UnreadableTip") : Folder.FullPath;

    /// <summary>Hint text under the name: unreadable folder, link, cloud-only files.</summary>
    public string Note
    {
        get
        {
            if (Folder is null)
            {
                return _snapshot.FailedDirectories > 0
                    ? LocationUi.Format("Storage_NoteUnreadableCount", _snapshot.FailedDirectories)
                    : string.Empty;
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

    /// <summary>Bytes the line stands for. The top line includes the unreadable part, so the shares below add up to 100 %.</summary>
    private long DisplaySize => Folder is null
        ? _snapshot.UnreadableBytes ?? 0
        : IsRoot ? Folder.Size + (_snapshot.UnreadableBytes ?? 0) : Folder.Size;

    public string SizeText => ByteSize.Format(DisplaySize);

    /// <summary>Share of the line in its parent (the top line: 1).</summary>
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

    public string PercentText => Fraction.ToString("P0", CultureInfo.CurrentCulture);

    public string FilesText => Folder is null ? string.Empty : Folder.FileCount.ToString("N0", CultureInfo.CurrentCulture);

    public string FoldersText => Folder is null ? string.Empty : Folder.FolderCount.ToString("N0", CultureInfo.CurrentCulture);

    public string DateText => Folder?.LastWriteUtc is { } utc
        ? utc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>Recomputes everything shown (after something below was moved to the Recycle Bin).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>
    /// Takes a line out of the tree and refreshes everything whose numbers changed: the lines above it and the shares of
    /// all lines beside them. Call after the folder was removed from the snapshot.
    /// </summary>
    public void RemoveFromTree()
    {
        if (Parent is null)
        {
            return;
        }

        Parent._children?.Remove(this);
        for (var ancestor = Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.Refresh();
            ancestor.RefreshChildren();
        }
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

    private ObservableCollection<SpaceRow> BuildChildren()
    {
        var rows = new List<SpaceRow>();
        if (Folder is not null)
        {
            rows.AddRange(Folder.Children.Select(c => new SpaceRow(_snapshot, c, this)));
            if (IsRoot && _snapshot.UnreadableBytes is not null)
            {
                rows.Add(new SpaceRow(_snapshot, null, this));
            }
        }

        // Largest first; the stable sort keeps the order of folders of equal size.
        return new ObservableCollection<SpaceRow>(rows.OrderByDescending(r => r.DisplaySize));
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
