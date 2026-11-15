using System.Text;

namespace FileDetective.Core;

/// <summary>Why an image is not read for text (stage 11, PLAN.md 11.3).</summary>
public enum ImageSkipReason
{
    /// <summary>The image is read.</summary>
    None,

    /// <summary>Not one of the image endings.</summary>
    NotAnImage,

    /// <summary><c>*.thumb.jpg</c>: a preview written by another program.</summary>
    Thumbnail,

    /// <summary>Below 5 KB.</summary>
    TooSmall,

    /// <summary>Above 50 MB.</summary>
    TooLarge,

    /// <summary>Only in the cloud, or fetched on open: never opened, because that would start a download.</summary>
    CloudOnly,
}

/// <summary>Which files the image text reads (PLAN.md 11.3, "Dateien").</summary>
public static class ImageTextRules
{
    /// <summary>Smallest file that is read: 5 KB.</summary>
    public const long MinSize = 5 * 1024;

    /// <summary>Largest file that is read: 50 MB.</summary>
    public const long MaxSize = 50L * 1024 * 1024;

    /// <summary>The endings that are read (a GIF only with its first picture).</summary>
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"];

    private const string ThumbnailEnding = ".thumb.jpg";

    /// <summary>True if the name has one of the image endings and is not a <c>*.thumb.jpg</c>.</summary>
    public static bool IsImageName(string? name) => NameReason(name) == ImageSkipReason.None;

    /// <summary>Decides from name, size and attributes alone (no file access) whether the image is read.</summary>
    public static ImageSkipReason Check(string? name, long size, FileAttributes attributes)
    {
        var reason = NameReason(name);
        if (reason != ImageSkipReason.None)
        {
            return reason;
        }

        // Cloud first: such a file is never opened, whatever its size says.
        if (DuplicateFinder.MustNotOpen(attributes))
        {
            return ImageSkipReason.CloudOnly;
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            return ImageSkipReason.NotAnImage;
        }

        if (size < MinSize)
        {
            return ImageSkipReason.TooSmall;
        }

        return size > MaxSize ? ImageSkipReason.TooLarge : ImageSkipReason.None;
    }

    private static ImageSkipReason NameReason(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return ImageSkipReason.NotAnImage;
        }

        if (name.EndsWith(ThumbnailEnding, StringComparison.OrdinalIgnoreCase))
        {
            return ImageSkipReason.Thumbnail;
        }

        foreach (string extension in Extensions)
        {
            if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return ImageSkipReason.None;
            }
        }

        return ImageSkipReason.NotAnImage;
    }
}

/// <summary>Result of <see cref="ImageTextFolders.Check"/>.</summary>
public enum ImageFolderCheck
{
    /// <summary>A local folder that can be added.</summary>
    Ok,

    /// <summary>Not an absolute drive or UNC path.</summary>
    Invalid,

    /// <summary>A UNC path, a network drive or a removable drive: refused, because every image would have to be opened.</summary>
    NotLocal,

    /// <summary>The folder is in the list already.</summary>
    AlreadyInList,
}

/// <summary>The folders whose images are read (PLAN.md 11.3, "Ordner").</summary>
public static class ImageTextFolders
{
    /// <summary>
    /// The folders in force: <see cref="AppSettings.OcrFolders"/>, or only the Windows Screenshots folder while the list
    /// was never set (<paramref name="screenshotsFolder"/>, null if Windows does not know it).
    /// </summary>
    public static IReadOnlyList<string> Effective(AppSettings settings, Func<string?> screenshotsFolder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(screenshotsFolder);
        if (settings.OcrFolders is { } folders)
        {
            return folders;
        }

        return LocationRules.Normalize(screenshotsFolder()) is { } path && !path.StartsWith(@"\\", StringComparison.Ordinal)
            ? [path]
            : [];
    }

    /// <summary>
    /// Checks a folder the user wants to add. Only local folders are allowed: a UNC path, a network drive or a removable
    /// drive is refused (PLAN.md 2.1: on shares no file is ever opened). Does not touch the file system.
    /// </summary>
    public static ImageFolderCheck Check(string? text, DriveDiscovery drives, IReadOnlyList<string> existing, out string? normalized)
    {
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(existing);
        normalized = null;
        if (drives.Describe(text) is not { } candidate)
        {
            return ImageFolderCheck.Invalid;
        }

        if (candidate.Kind != RootKind.Local || candidate.Path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return ImageFolderCheck.NotLocal;
        }

        normalized = candidate.Path;
        string path = candidate.Path;
        return existing.Any(e => string.Equals(LocationRules.Normalize(e), path, StringComparison.OrdinalIgnoreCase))
            ? ImageFolderCheck.AlreadyInList
            : ImageFolderCheck.Ok;
    }

    /// <summary>True if <paramref name="path"/> is one of the folders or lies below one (case-insensitive, at path boundaries).</summary>
    public static bool IsBelowAny(string path, IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        if (LocationRules.Normalize(path) is not { } normalized)
        {
            return false;
        }

        foreach (string folder in folders)
        {
            if (LocationRules.Normalize(folder) is { } f && LocationRules.ContainsOrEquals(f, normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The folders that lie in none of <paramref name="roots"/> and exist: they become locations of their own, like the
    /// Start menu folders (PLAN.md 7.3). UNC paths are never returned.
    /// </summary>
    public static IReadOnlyList<string> MissingLocations(
        IReadOnlyList<string> folders, IReadOnlyList<RootInfo> roots, Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(directoryExists);
        var missing = new List<string>();
        foreach (string raw in folders)
        {
            if (LocationRules.Normalize(raw) is not { } path || path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = new LocationCandidate(path, null, RootKind.Local);
            bool covered = roots.Any(r => LocationRules.Compare(LocationCandidate.FromRoot(r), candidate)
                is LocationRelation.Same or LocationRelation.FirstContainsSecond)
                || missing.Any(m => LocationRules.ContainsOrEquals(m, path));
            if (!covered && directoryExists(path))
            {
                // A folder that contains one already in the list replaces it (no nested locations).
                missing.RemoveAll(m => LocationRules.ContainsOrEquals(path, m));
                missing.Add(path);
            }
        }

        return missing;
    }

    /// <summary>Normalized, without blanks, without UNC paths and without duplicates (case-insensitive), in input order.</summary>
    public static IReadOnlyList<string> Clean(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var result = new List<string>();
        foreach (string raw in folders)
        {
            if (LocationRules.Normalize(raw) is { } path
                && !path.StartsWith(@"\\", StringComparison.Ordinal)
                && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(path);
            }
        }

        return result;
    }
}

/// <summary>
/// Reads the text in an image (stage 11). The real one is <see cref="WindowsImageTextRecognizer"/> (the text recognition
/// built into Windows, offline); tests use a stand-in that counts its calls.
/// </summary>
public interface IImageTextRecognizer
{
    /// <summary>True if Windows has a recognition language for the user profile; otherwise nothing can be read.</summary>
    bool IsAvailable { get; }

    /// <summary>The recognized text, lines separated by line breaks; empty if the image has none.</summary>
    /// <exception cref="InvalidDataException">The image cannot be decoded.</exception>
    string Recognize(Stream image, CancellationToken cancellationToken);
}

/// <summary>A text place around the first hit, cut into plain and highlighted pieces (a row of mode Image text).</summary>
public sealed record ImageTextSnippet(string Text, IReadOnlyList<TextSegment> Segments)
{
    /// <summary>Characters shown before the first hit.</summary>
    public const int Before = 40;

    /// <summary>Length of the text place.</summary>
    public const int Length = 140;

    /// <summary>
    /// The place of <paramref name="text"/> around the first hit of the terms of <paramref name="query"/> (the beginning
    /// of the text without a hit or without terms). Line breaks and runs of white space become single spaces; a cut at
    /// either end is marked with <c>…</c> and moved to a word boundary when one is near.
    /// </summary>
    public static ImageTextSnippet Create(string text, SearchQuery? query)
    {
        ArgumentNullException.ThrowIfNull(text);
        string flat = Flatten(text);
        if (flat.Length == 0)
        {
            return new ImageTextSnippet(string.Empty, []);
        }

        var ranges = query is null || query.TermBytes.Length == 0 ? [] : MatchHighlighter.Find(flat, query.TermBytes);
        int start = 0;
        if (ranges.Count > 0 && ranges[0].Start > Before)
        {
            start = ranges[0].Start - Before;
            int space = flat.IndexOf(' ', start);
            if (space >= 0 && space < ranges[0].Start && space - start < 15)
            {
                start = space + 1;
            }
        }

        int end = Math.Min(flat.Length, start + Length);
        if (ranges.Count > 0 && ranges[0].End > end)
        {
            end = Math.Min(flat.Length, ranges[0].End);
        }

        if (end < flat.Length)
        {
            int space = flat.LastIndexOf(' ', end - 1, end - start);
            int firstEnd = ranges.Count > 0 ? ranges[0].End : start;
            if (space > start && space >= firstEnd && end - space < 15)
            {
                end = space;
            }
        }

        string prefix = start > 0 ? "…" : string.Empty;
        string suffix = end < flat.Length ? "…" : string.Empty;
        var segments = new List<TextSegment>();
        if (prefix.Length > 0)
        {
            segments.Add(new TextSegment(prefix, false));
        }

        int position = start;
        foreach (var range in ranges)
        {
            int from = Math.Max(range.Start, start);
            int to = Math.Min(range.End, end);
            if (to <= from)
            {
                continue;
            }

            if (from > position)
            {
                segments.Add(new TextSegment(flat[position..from], false));
            }

            segments.Add(new TextSegment(flat[from..to], true));
            position = to;
        }

        if (position < end)
        {
            segments.Add(new TextSegment(flat[position..end], false));
        }

        if (suffix.Length > 0)
        {
            segments.Add(new TextSegment(suffix, false));
        }

        return new ImageTextSnippet(prefix + flat[start..end] + suffix, segments);
    }

    /// <summary>Line breaks, tabs and runs of white space become single spaces; trimmed.</summary>
    public static string Flatten(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
            {
                sb.Append(' ');
                space = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}

/// <summary>
/// The recognized texts in memory (PLAN.md 11.3, "Speicher"): per entry the text in search form (6.2) and the last write
/// time of the image. Thread-safe. A few thousand screenshots take a few MB.
/// </summary>
/// <remarks>
/// Stage 13 (PLAN.md 11.4) adds the notes of the user: kept apart from the recognized texts, so <see cref="Clear"/>,
/// <see cref="Count"/>, <see cref="Contains"/> and <see cref="Ids"/> concern the recognized texts only, while
/// <see cref="Find"/> searches both: a term may stand in the note and another in the text.
/// </remarks>
public sealed class ImageTextIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<int, (byte[] Form, long MTimeTicks)> _texts = [];
    private readonly Dictionary<int, (string Text, byte[] Form, long MTimeTicks)> _notes = [];

    /// <summary>Number of images with a recognized text (an empty text counts: the image was read).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _texts.Count;
            }
        }
    }

    /// <summary>The search form of a text: lines joined by single spaces, then <see cref="SearchNormalizer.Normalize"/>.</summary>
    public static byte[] SearchForm(string text) => SearchNormalizer.Normalize(ImageTextSnippet.Flatten(text));

    public void Set(int entryId, string text, long mtimeTicks)
    {
        ArgumentNullException.ThrowIfNull(text);
        var form = SearchForm(text);
        lock (_gate)
        {
            _texts[entryId] = (form, mtimeTicks);
        }
    }

    public bool Contains(int entryId)
    {
        lock (_gate)
        {
            return _texts.ContainsKey(entryId);
        }
    }

    public bool Remove(int entryId)
    {
        lock (_gate)
        {
            return _texts.Remove(entryId);
        }
    }

    public void RemoveAll(IEnumerable<int> entryIds)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        lock (_gate)
        {
            foreach (int id in entryIds)
            {
                _texts.Remove(id);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _texts.Clear();
        }
    }

    /// <summary>The entry numbers in memory.</summary>
    public IReadOnlyList<int> Ids()
    {
        lock (_gate)
        {
            return [.. _texts.Keys];
        }
    }

    /// <summary>Sets the note of an image (stage 13); <paramref name="mtimeTicks"/> orders it when it has no recognized text.</summary>
    public void SetNote(int entryId, string text, long mtimeTicks)
    {
        ArgumentNullException.ThrowIfNull(text);
        var form = SearchForm(text);
        lock (_gate)
        {
            _notes[entryId] = (text, form, mtimeTicks);
        }
    }

    /// <summary>The note of an image, or null.</summary>
    public string? GetNote(int entryId)
    {
        lock (_gate)
        {
            return _notes.TryGetValue(entryId, out var note) ? note.Text : null;
        }
    }

    public bool RemoveNote(int entryId)
    {
        lock (_gate)
        {
            return _notes.Remove(entryId);
        }
    }

    /// <summary>Forgets all notes in memory (the stored ones stay).</summary>
    public void ClearNotes()
    {
        lock (_gate)
        {
            _notes.Clear();
        }
    }

    /// <summary>The text shown for an image: the note, then the recognized text, each only if present.</summary>
    public static string Combine(string? note, string? text)
    {
        bool hasNote = !string.IsNullOrWhiteSpace(note);
        bool hasText = !string.IsNullOrWhiteSpace(text);
        return hasNote && hasText ? note!.Trim() + "\n" + text : hasNote ? note!.Trim() : text ?? string.Empty;
    }

    /// <summary>
    /// Entry numbers of all images whose text and note together contain every term of <paramref name="query"/> (each term in
    /// the one or the other), newest image first (equal times: higher entry number first). Without terms: all images with a
    /// text or a note, newest first.
    /// </summary>
    public IReadOnlyList<int> Find(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var hits = new List<(int Id, long MTime)>();
        lock (_gate)
        {
            int n = 0;
            foreach (var (id, (form, mtime)) in _texts)
            {
                if ((++n & 0x3FF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                byte[]? note = _notes.TryGetValue(id, out var noted) ? noted.Form : null;
                if (MatchesAll(query, form, note))
                {
                    hits.Add((id, mtime));
                }
            }

            foreach (var (id, (_, form, mtime)) in _notes)
            {
                if (!_texts.ContainsKey(id) && MatchesAll(query, form, null))
                {
                    hits.Add((id, mtime));
                }
            }
        }

        hits.Sort(static (a, b) => a.MTime != b.MTime ? b.MTime.CompareTo(a.MTime) : b.Id.CompareTo(a.Id));
        return [.. hits.Select(h => h.Id)];
    }

    private static bool MatchesAll(SearchQuery query, byte[] form, byte[]? other)
    {
        foreach (var term in query.TermBytes)
        {
            if (form.AsSpan().IndexOf(term) < 0 && (other is null || other.AsSpan().IndexOf(term) < 0))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Search of mode Image text (PLAN.md 11.3, "Suche"): images whose text contains every term, newest first, at most
/// <see cref="SearchEngine.MaxResults"/>; with an empty input the newest images. Type filters (<c>.png</c>) apply to the
/// file name. Only images that still exist and lie in one of the folders count, so a moved image drops out at once.
/// </summary>
public sealed class ImageTextSearch(
    ImageTextIndex texts,
    IEntryResolver resolver,
    Func<IReadOnlyCollection<int>, IReadOnlyDictionary<int, StoredImageText>> loadTexts,
    Func<IReadOnlyList<string>> folders)
{
    private const int ResolveChunk = 200;

    public SearchResult Search(SearchQuery query, int max = SearchEngine.MaxResults, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var ids = texts.Find(query, cancellationToken);
        if (ids.Count == 0)
        {
            return SearchResult.Empty;
        }

        var inFolders = folders();
        var hits = new List<SearchHit>(Math.Min(max, ids.Count));
        int dropped = 0;
        int position = 0;
        while (position < ids.Count && hits.Count < max)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = ids.Skip(position).Take(ResolveChunk).ToList();
            position += chunk.Count;
            var details = resolver.Resolve(chunk, cancellationToken);
            var kept = new List<(int Id, EntryDetails Details)>();
            for (int i = 0; i < chunk.Count; i++)
            {
                if (details[i] is { IsDirectory: false } d
                    && ImageTextFolders.IsBelowAny(d.FullPath, inFolders)
                    && FitsTypeFilters(d.Name, query))
                {
                    kept.Add((chunk[i], d));
                }
                else
                {
                    dropped++;
                }
            }

            var stored = loadTexts([.. kept.Select(k => k.Id)]);
            foreach (var (id, d) in kept)
            {
                if (hits.Count >= max)
                {
                    break;
                }

                string text = ImageTextIndex.Combine(texts.GetNote(id), stored.TryGetValue(id, out var row) ? row.Text : null);
                hits.Add(new SearchHit(id, d, 0, 0, ImageTextSnippet.Create(text, query)));
            }
        }

        return new SearchResult(hits, ids.Count - dropped);
    }

    internal static bool FitsTypeFilters(string name, SearchQuery query)
    {
        if (query.TypeFilterBytes.Length == 0)
        {
            return true;
        }

        var form = SearchNormalizer.Normalize(name);
        return query.TypeFilterBytes.Any(f => form.AsSpan().EndsWith(f));
    }
}
