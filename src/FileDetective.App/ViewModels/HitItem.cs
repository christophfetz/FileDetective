using System.IO;
using System.Windows.Media;
using FileDetective.App.Interop;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>One row of the hit list.</summary>
internal sealed class HitItem
{
    /// <summary>Characters of the folder path shown before it is cut in the middle; fits the 300 DIP path column.</summary>
    public const int FolderDisplayLength = 44;

    private HitItem(SearchHit hit)
    {
        var d = hit.Details;
        EntryId = hit.EntryId;
        FullPath = d.FullPath;
        IsDirectory = d.IsDirectory;

        // The root entry of a location carries its whole path as name; show its last folder name instead.
        Name = d.Name.Contains('\\') || d.Name.Contains('/')
            ? (Path.GetFileName(d.Name.TrimEnd('\\', '/')) is { Length: > 0 } last ? last : d.Name)
            : d.Name;

        string? parent = Path.GetDirectoryName(d.FullPath.TrimEnd('\\', '/'));
        FolderPath = string.IsNullOrEmpty(parent) ? string.Empty : parent.TrimEnd('\\') + "\\";
        FolderDisplay = PathShortener.ShortenMiddle(FolderPath, FolderDisplayLength);
        DateText = d.LastWriteUtc.ToLocalTime().ToString("d");
        Icon = FileIcons.For(Name, d.IsDirectory);
    }

    public int EntryId { get; }

    public string FullPath { get; }

    public string Name { get; }

    public string FolderPath { get; }

    public string FolderDisplay { get; }

    public string DateText { get; }

    public bool IsDirectory { get; }

    public ImageSource? Icon { get; }

    /// <summary>Creates the row; touches the icon cache, so call it on the UI thread.</summary>
    public static HitItem From(SearchHit hit) => new(hit);
}
