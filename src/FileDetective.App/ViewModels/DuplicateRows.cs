using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>A line of the duplicate list: a group header or one of its files.</summary>
internal abstract class DuplicateRow : ObservableObject
{
}

/// <summary>Header of a group: size, number of files and the wasted space.</summary>
internal sealed class DuplicateGroupRow(DuplicateGroup group) : DuplicateRow
{
    public DuplicateGroup Group { get; } = group;

    public string Title => string.Format(
        CultureInfo.CurrentCulture,
        Strings.Get(Group.IsVerified ? "Dup_GroupTitle" : "Dup_GroupTitleUnchecked"),
        Group.Files.Count, ByteSize.Format(Group.Size), ByteSize.Format(Group.WastedBytes));
}

/// <summary>
/// One file of a group with its check box. Setting <see cref="IsChecked"/> asks the group; if the group refuses (the last
/// unmarked file, a share, a removable drive) the box springs back.
/// </summary>
internal sealed class DuplicateFileRow(DuplicateFile file, Action changed) : DuplicateRow
{
    public DuplicateFile File { get; } = file;

    public string Name => File.Name;

    public string FolderPath => File.Folder;

    public string DateText => File.LastWriteUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string SizeText => ByteSize.Format(File.Size);

    /// <summary>Check boxes exist only for files that may be moved at all.</summary>
    public bool ShowCheckBox => File.Group.IsVerified && File.CanRecycle;

    public string? Note => File.Group.IsVerified && !File.CanRecycle ? Strings.Get("Dup_NoteRemovable") : null;

    public bool IsChecked
    {
        get => File.IsMarked;
        set
        {
            File.Group.SetMarked(File, value);
            OnPropertyChanged();
            changed();
        }
    }

    /// <summary>After a helper changed the marks.</summary>
    public void Refresh() => OnPropertyChanged(nameof(IsChecked));
}
