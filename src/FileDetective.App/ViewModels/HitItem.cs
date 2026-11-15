using System.IO;
using System.Windows.Media;
using FileDetective.App.Interop;
using FileDetective.Core;

namespace FileDetective.App.ViewModels;

/// <summary>One row of the hit list.</summary>
internal class HitItem
{
    /// <summary>Characters of the folder path shown before it is cut in the middle; fits the 300 DIP path column.</summary>
    public const int FolderDisplayLength = 44;

    protected HitItem(SearchHit hit, bool unreachable, SearchQuery? query)
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
    public static HitItem From(SearchHit hit, bool unreachable, SearchQuery? query = null)
        => hit.Snippet is not null ? new ImageHitItem(hit, unreachable, query) : new HitItem(hit, unreachable, query);
}

/// <summary>
/// A row of mode Image text (PLAN.md 11.3): preview, name and the text place around the first hit with the terms marked.
/// The preview arrives later from <see cref="Interop.Thumbnails"/>; until then the file type icon stands in.
/// </summary>
internal sealed class ImageHitItem : HitItem, System.ComponentModel.INotifyPropertyChanged
{
    private ImageSource? _thumbnail;

    public ImageHitItem(SearchHit hit, bool unreachable, SearchQuery? query)
        : base(hit, unreachable, query)
    {
        var snippet = hit.Snippet ?? new ImageTextSnippet(string.Empty, []);
        SnippetSegments = snippet.Segments.Count > 0
            ? snippet.Segments
            : [new TextSegment(Strings.Get("Hit_NoText"), false)];
        LastWriteUtc = hit.Details.LastWriteUtc;
        DateTimeText = hit.Details.LastWriteUtc.ToLocalTime().ToString("g");
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The text place, cut into plain and marked pieces; "no text recognized" if the image has none.</summary>
    public IReadOnlyList<TextSegment> SnippetSegments { get; }

    public DateTime LastWriteUtc { get; }

    /// <summary>Date and time of the image (screenshots of one day differ only in the time).</summary>
    public string DateTimeText { get; }

    /// <summary>The preview from Windows, or null while it loads or if there is none (then the icon shows).</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Preview)));
        }
    }

    /// <summary>What the row shows on the left: the preview, else the file type icon.</summary>
    public ImageSource? Preview => _thumbnail ?? Icon;
}

