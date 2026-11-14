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

    private HitItem(SearchHit hit, bool unreachable, SearchQuery? query)
    {
        var d = hit.Details;
        EntryId = hit.EntryId;
        RootId = d.RootId;
        FullPath = d.FullPath;
        IsDirectory = d.IsDirectory;
        IsUnreachable = unreachable;
        NotConnectedText = unreachable ? Strings.Get("Hit_NotConnected") : string.Empty;

        // The root entry of a location carries its whole path as name; show its last folder name instead.
        Name = d.Name.Contains('\\') || d.Name.Contains('/')
            ? (Path.GetFileName(d.Name.TrimEnd('\\', '/')) is { Length: > 0 } last ? last : d.Name)
            : d.Name;

        NameSegments = MatchHighlighter.Split(Name, query);

        string? parent = Path.GetDirectoryName(d.FullPath.TrimEnd('\\', '/'));
        FolderPath = string.IsNullOrEmpty(parent) ? string.Empty : parent.TrimEnd('\\') + "\\";
        FolderDisplay = PathShortener.ShortenMiddle(FolderPath, FolderDisplayLength);
        DateText = d.LastWriteUtc.ToLocalTime().ToString("d");
        Icon = FileIcons.For(Name, d.IsDirectory);
    }

    public int EntryId { get; }

    /// <summary>The location of the hit.</summary>
    public byte RootId { get; }

    /// <summary>
    /// The location is known to be unreachable (PLAN.md 8.4): the row is greyed out with "not connected", and only
    /// "Copy path" is possible.
    /// </summary>
    public bool IsUnreachable { get; }

    public string NotConnectedText { get; }

    public string FullPath { get; }

    public string Name { get; }

    /// <summary>The name cut into plain and highlighted pieces (PLAN.md 8.4: the places where the terms were found).</summary>
    public IReadOnlyList<TextSegment> NameSegments { get; }

    public string FolderPath { get; }

    public string FolderDisplay { get; }

    public string DateText { get; }

    public bool IsDirectory { get; }

    public ImageSource? Icon { get; }

    /// <summary>Creates the row; touches the icon cache, so call it on the UI thread.</summary>
    public static HitItem From(SearchHit hit, bool unreachable, SearchQuery? query = null) => new(hit, unreachable, query);
}

