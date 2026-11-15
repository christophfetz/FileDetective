namespace FileDetective.Core;

/// <summary>
/// One image of the screenshot history (stage 12, PLAN.md 11.3). Comes from the directory, no file is opened for it.
/// </summary>
/// <param name="Folder">The configured image text folder the image lies in (the most specific one).</param>
/// <param name="HasText">The text of the image was read (an empty text counts).</param>
public sealed record HistoryImage(
    int EntryId, string Name, string FullPath, string Folder, DateTime LastWriteUtc, long Size, FileAttributes Attributes, bool HasText)
{
    /// <summary>The file is only in the cloud or fetched on open: it is never opened, so it gets no preview and no "copy image".</summary>
    public bool IsCloud => DuplicateFinder.MustNotOpen(Attributes);

    /// <summary>A preview may be made by Windows (local folders only; never for a cloud file, that would start a download).</summary>
    public bool CanThumbnail => !IsCloud;

    /// <summary>"Copy image" has to decode the file, so it is not offered for a cloud file.</summary>
    public bool CanCopyImage => !IsCloud;
}

/// <summary>Where the history takes its images from: the directory. Tests use a stand-in.</summary>
public interface IImageHistorySource
{
    /// <summary>The images below the folders as the directory knows them. Lists no folder and opens no file.</summary>
    IReadOnlyList<HistoryImage> Read(IReadOnlyList<string> folders, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IImageHistorySource"/> over the <see cref="IndexStore"/>: reads the stored files below the folders (one query
/// per folder), keeps those of local locations that are images the text recognition reads or would read (an image of the
/// allowed endings and sizes; a cloud-only image stays, it is shown with a placeholder), and builds the path from the
/// folder path of the parent.
/// </summary>
public sealed class StoreImageHistorySource(IndexStore store, IEntryResolver resolver) : IImageHistorySource
{
    private const int ResolveChunk = 500;

    public IReadOnlyList<HistoryImage> Read(IReadOnlyList<string> folders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var localRoots = store.GetRoots().Where(r => r.Kind == RootKind.Local).Select(r => r.Id).ToHashSet();
        var found = new Dictionary<int, (StoredEntry Entry, string Folder)>();
        foreach (string raw in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LocationRules.Normalize(raw) is not { } folder
                || folder.StartsWith(@"\\", StringComparison.Ordinal)
                || store.FindEntryId(folder) is not { } folderId)
            {
                continue;
            }

            store.ReadFilesBelow(folderId, entry =>
            {
                if (!localRoots.Contains(entry.RootId)
                    || ImageTextRules.Check(entry.Name, entry.Size, entry.Attributes) is not (ImageSkipReason.None or ImageSkipReason.CloudOnly))
                {
                    return;
                }

                // Folders inside each other: the image belongs to the most specific one.
                if (!found.TryGetValue(entry.Id, out var known) || folder.Length > known.Folder.Length)
                {
                    found[entry.Id] = (entry, folder);
                }
            }, cancellationToken);
        }

        var parentIds = found.Values.Select(v => v.Entry.ParentId).Distinct().Order().ToList();
        var parentPaths = new Dictionary<int, string>(parentIds.Count);
        for (int i = 0; i < parentIds.Count; i += ResolveChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = parentIds.GetRange(i, Math.Min(ResolveChunk, parentIds.Count - i));
            var details = resolver.Resolve(chunk, cancellationToken);
            for (int k = 0; k < chunk.Count; k++)
            {
                if (details[k] is { } d)
                {
                    parentPaths[chunk[k]] = d.FullPath;
                }
            }
        }

        var result = new List<HistoryImage>(found.Count);
        foreach (var (entry, folder) in found.Values)
        {
            if (!parentPaths.TryGetValue(entry.ParentId, out string? parent))
            {
                continue; // vanished meanwhile
            }

            string path = parent.EndsWith('\\') ? parent + entry.Name : parent + "\\" + entry.Name;
            result.Add(new HistoryImage(
                entry.Id, entry.Name, path, folder, DateTime.UnixEpoch.AddSeconds(entry.MTimeUnixSeconds), entry.Size, entry.Attributes, false));
        }

        return result;
    }
}

/// <summary>All images of the history, newest first, and the folders they were read for.</summary>
public sealed record ImageHistorySnapshot(IReadOnlyList<HistoryImage> Images, IReadOnlyList<string> Folders)
{
    public static ImageHistorySnapshot Empty { get; } = new([], []);
}

/// <summary>The images that pass the search text and the folder filter, newest first; <see cref="Query"/> is for highlighting.</summary>
public sealed record ImageHistoryResult(IReadOnlyList<HistoryImage> Images, SearchQuery Query, int Total);

/// <summary>
/// The screenshot history (PLAN.md 11.3, stage 12): the images of the image text folders from the directory, newest first,
/// found by the same text search as the search bar. Nothing is listed on disk and no file is opened; if the image text is
/// switched off, the images of the folders are still shown (they are in the directory) but have no text, so a search
/// by text finds nothing.
/// </summary>
public sealed class ImageHistory(IImageHistorySource source, ImageTextIndex texts, Func<IReadOnlyList<string>> folders)
{
    /// <summary>Newest first; images of the same time: higher entry number first (as <see cref="ImageTextIndex.Find"/>).</summary>
    public static IReadOnlyList<HistoryImage> NewestFirst(IEnumerable<HistoryImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        var list = images.ToList();
        list.Sort(static (a, b) => a.LastWriteUtc != b.LastWriteUtc ? b.LastWriteUtc.CompareTo(a.LastWriteUtc) : b.EntryId.CompareTo(a.EntryId));
        return list;
    }

    /// <summary>
    /// True if a change of the directory can alter the list: an image written or updated (an update carries no name and is
    /// taken as possible), something removed, moved or renamed, or the whole directory emptied.
    /// </summary>
    public static bool IsRelevant(IndexChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return change.Reset || change.Relocated || change.Deleted.Count > 0
            || change.Written.Any(w => w.Name is null || ImageTextRules.IsImageName(w.Name));
    }

    /// <summary>Reads the images from the directory (newest first) and marks those whose text is known.</summary>
    public ImageHistorySnapshot Load(CancellationToken cancellationToken = default)
    {
        var inFolders = folders();
        var images = NewestFirst(source.Read(inFolders, cancellationToken)
            .Select(i => i with { HasText = texts.Contains(i.EntryId) }));
        return new ImageHistorySnapshot(images, inFolders);
    }

    /// <summary>
    /// Applies the search text (every term in the text of the image, <c>müller</c> = <c>mueller</c>, a type filter such as
    /// <c>.png</c> on the name) and the folder filter (null = all folders) to a snapshot.
    /// </summary>
    public ImageHistoryResult Filter(
        ImageHistorySnapshot snapshot, string? text, string? folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var query = SearchQuery.Parse(text, SearchMode.ImageText);
        IEnumerable<HistoryImage> images = snapshot.Images;
        if (folder is not null)
        {
            string? wanted = LocationRules.Normalize(folder);
            images = images.Where(i => string.Equals(i.Folder, wanted, StringComparison.OrdinalIgnoreCase));
        }

        if (query.TermBytes.Length > 0)
        {
            var matching = texts.Find(query, cancellationToken).ToHashSet();
            images = images.Where(i => matching.Contains(i.EntryId));
        }

        if (query.TypeFilterBytes.Length > 0)
        {
            images = images.Where(i => ImageTextSearch.FitsTypeFilters(i.Name, query));
        }

        return new ImageHistoryResult(NewestFirst(images), query, snapshot.Images.Count);
    }
}

/// <summary>The images of one calendar day (in a given time zone), newest first.</summary>
public sealed record HistoryDay(DateOnly Day, IReadOnlyList<HistoryImage> Images);

/// <summary>How a day is named in the list.</summary>
public enum HistoryDayKind
{
    Today,
    Yesterday,
    Other,
}

/// <summary>A row of the history grid: a day header or a row of up to "columns" previews.</summary>
public abstract record HistoryRow;

public sealed record HistoryHeaderRow(DateOnly Day, int Count) : HistoryRow;

public sealed record HistoryTileRow(DateOnly Day, IReadOnlyList<HistoryImage> Images) : HistoryRow;

/// <summary>Direction of a keyboard move in the grid.</summary>
public enum HistoryMove
{
    Left,
    Right,
    Up,
    Down,
    PageUp,
    PageDown,
    Home,
    End,
}

/// <summary>Grouping by days, rows of the grid and moving the selection (all without a window, so tests can check them).</summary>
public static class HistoryLayout
{
    /// <summary>
    /// Groups the images by calendar day of <paramref name="zone"/>, newest day first, the images of a day newest first. The
    /// zone is given, so tests fix it and a change between summer and winter time makes a 25 or 23 hour day, not a
    /// wrong day.
    /// </summary>
    public static IReadOnlyList<HistoryDay> GroupByDay(IEnumerable<HistoryImage> images, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(zone);
        var days = new List<HistoryDay>();
        DateOnly? current = null;
        List<HistoryImage>? bucket = null;
        foreach (var image in ImageHistory.NewestFirst(images))
        {
            var utc = DateTime.SpecifyKind(image.LastWriteUtc, DateTimeKind.Utc);
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, zone));
            if (current != day)
            {
                bucket = [];
                days.Add(new HistoryDay(day, bucket));
                current = day;
            }

            bucket!.Add(image);
        }

        return days;
    }

    public static DateOnly Today(TimeProvider time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).DateTime);
    }

    public static HistoryDayKind KindOf(DateOnly day, DateOnly today)
        => day == today ? HistoryDayKind.Today : day == today.AddDays(-1) ? HistoryDayKind.Yesterday : HistoryDayKind.Other;

    /// <summary>How many previews of <paramref name="tileWidth"/> fit into <paramref name="width"/> (at least 1).</summary>
    public static int ColumnsFor(double width, double tileWidth)
        => tileWidth <= 0 || double.IsNaN(width) ? 1 : Math.Max(1, (int)Math.Floor(width / tileWidth));

    /// <summary>A header per day, then the images of the day in rows of <paramref name="columns"/>.</summary>
    public static IReadOnlyList<HistoryRow> BuildRows(IReadOnlyList<HistoryDay> days, int columns)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var rows = new List<HistoryRow>();
        foreach (var day in days)
        {
            rows.Add(new HistoryHeaderRow(day.Day, day.Images.Count));
            for (int i = 0; i < day.Images.Count; i += columns)
            {
                rows.Add(new HistoryTileRow(day.Day, [.. day.Images.Skip(i).Take(columns)]));
            }
        }

        return rows;
    }

    /// <summary>
    /// The image reached from <paramref name="current"/> by <paramref name="move"/>; without a current image the first one
    /// (the last one for End). Left and Right go through the days in order, Up and Down keep the column, page moves go
    /// <paramref name="pageRows"/> rows of previews. Null if there is no image at all.
    /// </summary>
    public static int? Move(IReadOnlyList<HistoryRow> rows, int? current, HistoryMove move, int pageRows = 4)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var tileRows = rows.OfType<HistoryTileRow>().ToList();
        if (tileRows.Count == 0)
        {
            return null;
        }

        int rowIndex = -1;
        int col = -1;
        if (current is { } id)
        {
            for (int r = 0; r < tileRows.Count && rowIndex < 0; r++)
            {
                for (int c = 0; c < tileRows[r].Images.Count; c++)
                {
                    if (tileRows[r].Images[c].EntryId == id)
                    {
                        rowIndex = r;
                        col = c;
                        break;
                    }
                }
            }
        }

        int first = tileRows[0].Images[0].EntryId;
        int last = tileRows[^1].Images[^1].EntryId;
        if (rowIndex < 0)
        {
            return move is HistoryMove.End or HistoryMove.PageDown ? last : first;
        }

        switch (move)
        {
            case HistoryMove.Home:
                return first;
            case HistoryMove.End:
                return last;
            case HistoryMove.Left:
                return col > 0
                    ? tileRows[rowIndex].Images[col - 1].EntryId
                    : rowIndex > 0 ? tileRows[rowIndex - 1].Images[^1].EntryId : current;
            case HistoryMove.Right:
                return col < tileRows[rowIndex].Images.Count - 1
                    ? tileRows[rowIndex].Images[col + 1].EntryId
                    : rowIndex < tileRows.Count - 1 ? tileRows[rowIndex + 1].Images[0].EntryId : current;
        }

        int step = move switch
        {
            HistoryMove.Up => -1,
            HistoryMove.Down => 1,
            HistoryMove.PageUp => -Math.Max(1, pageRows),
            _ => Math.Max(1, pageRows),
        };
        int target = Math.Clamp(rowIndex + step, 0, tileRows.Count - 1);
        var images = tileRows[target].Images;
        return images[Math.Min(col, images.Count - 1)].EntryId;
    }
}

/// <summary>
/// Pixel positions of the rows (a header and a row of previews have fixed heights), so the window can create only the rows
/// in view, scroll an image into view and keep the place after the list was rebuilt.
/// </summary>
public sealed class HistoryRowMap
{
    private readonly double[] _tops;
    private readonly Dictionary<int, int> _rowOfImage = [];
    private readonly Dictionary<DateOnly, int> _rowOfDay = [];

    public HistoryRowMap(IReadOnlyList<HistoryRow> rows, double headerHeight, double tileRowHeight)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows = rows;
        HeaderHeight = headerHeight;
        TileRowHeight = tileRowHeight;
        _tops = new double[rows.Count];
        double y = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            _tops[i] = y;
            y += HeightOf(i);
            if (rows[i] is HistoryTileRow tiles)
            {
                foreach (var image in tiles.Images)
                {
                    _rowOfImage[image.EntryId] = i;
                }
            }
            else if (rows[i] is HistoryHeaderRow header)
            {
                _rowOfDay[header.Day] = i;
            }
        }

        TotalHeight = y;
    }

    public IReadOnlyList<HistoryRow> Rows { get; }

    public double HeaderHeight { get; }

    public double TileRowHeight { get; }

    public double TotalHeight { get; }

    public double Top(int row) => _tops[row];

    public double HeightOf(int row) => Rows[row] is HistoryHeaderRow ? HeaderHeight : TileRowHeight;

    /// <summary>The row that covers the vertical position <paramref name="y"/> (clamped to the first and last row); -1 if there are no rows.</summary>
    public int IndexAt(double y)
    {
        if (_tops.Length == 0)
        {
            return -1;
        }

        int index = Array.BinarySearch(_tops, y);
        if (index < 0)
        {
            index = ~index - 1;
        }

        return Math.Clamp(index, 0, _tops.Length - 1);
    }

    /// <summary>The first and last row that touch the vertical range; (0, -1) if there are no rows.</summary>
    public (int First, int Last) Visible(double top, double height)
    {
        if (_tops.Length == 0)
        {
            return (0, -1);
        }

        return (IndexAt(Math.Max(0, top)), IndexAt(Math.Max(0, top + height - 0.01)));
    }

    /// <summary>The row of previews with the image, or -1.</summary>
    public int RowOfImage(int entryId) => _rowOfImage.TryGetValue(entryId, out int row) ? row : -1;

    /// <summary>The header row of a day, or -1.</summary>
    public int RowOfDay(DateOnly day) => _rowOfDay.TryGetValue(day, out int row) ? row : -1;

    /// <summary>
    /// The scroll position that shows the row completely with the least movement from <paramref name="offset"/>
    /// (unchanged if it is in view already).
    /// </summary>
    public double OffsetToReveal(int row, double offset, double viewportHeight)
    {
        double top = Top(row);
        double bottom = top + HeightOf(row);
        if (top < offset)
        {
            return top;
        }

        return bottom > offset + viewportHeight ? Math.Max(0, bottom - viewportHeight) : offset;
    }

    /// <summary>
    /// The image at the top of the view (or the day, if a header is there) and how far the view is scrolled into its row,
    /// to find the place again after the list was rebuilt. Null at the very top or without rows: there the view stays at
    /// the top, so new images are seen.
    /// </summary>
    public HistoryAnchor? AnchorAt(double offset)
    {
        if (_tops.Length == 0 || offset <= 0.5)
        {
            return null;
        }

        int row = IndexAt(offset);
        double inside = Math.Max(0, offset - _tops[row]);
        return Rows[row] switch
        {
            HistoryTileRow tiles => new HistoryAnchor(tiles.Images[0].EntryId, null, inside),
            HistoryHeaderRow header => new HistoryAnchor(null, header.Day, inside),
            _ => null,
        };
    }

    /// <summary>The scroll position of an anchor in this layout: the row of the anchor image, else its day, else the top.</summary>
    public double OffsetOf(HistoryAnchor? anchor)
    {
        if (anchor is null || _tops.Length == 0)
        {
            return 0;
        }

        int row = anchor.EntryId is { } id ? RowOfImage(id) : -1;
        if (row < 0 && anchor.Day is { } day)
        {
            row = RowOfDay(day);
        }

        return row < 0 ? 0 : Math.Min(Top(row) + anchor.Inside, TotalHeight);
    }
}

/// <summary>The place in the list that was at the top of the view (see <see cref="HistoryRowMap.AnchorAt"/>).</summary>
public sealed record HistoryAnchor(int? EntryId, DateOnly? Day, double Inside);

/// <summary>
/// Runs an action at most once per <see cref="Interval"/> however often <see cref="Trigger"/> is called (the history
/// refreshes at most every 2 s while screenshots are being recognized). The first trigger after a quiet time runs at once;
/// triggers during the wait are absorbed into one run at the end of the interval, so the last change is never lost.
/// </summary>
public sealed class RefreshThrottle : IDisposable
{
    private readonly Action _action;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private DateTimeOffset? _lastRun;
    private bool _pending;
    private bool _disposed;

    public RefreshThrottle(TimeSpan interval, Action action, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, TimeSpan.Zero);
        Interval = interval;
        _action = action;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Interval { get; }

    /// <summary>Raised when the action threw.</summary>
    public event Action<Exception>? Failed;

    public void Trigger()
    {
        lock (_gate)
        {
            if (_pending || _disposed)
            {
                return;
            }

            _pending = true;
            var wait = _lastRun is { } last ? Interval - (_time.GetUtcNow() - last) : TimeSpan.Zero;
            if (wait > TimeSpan.Zero)
            {
                _timer.Change(wait, Timeout.InfiniteTimeSpan);
                return;
            }
        }

        Fire();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending = false;
        }

        _timer.Dispose();
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (!_pending || _disposed)
            {
                return;
            }

            _pending = false;
            _lastRun = _time.GetUtcNow();
        }

        try
        {
            _action();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
    }
}

/// <summary>Checks the shortcut of the history against the one of the search bar (both are global hotkeys of this program).</summary>
public static class HotkeyRules
{
    /// <summary>True if <paramref name="candidate"/> is the same combination as <paramref name="other"/> (null = none set).</summary>
    public static bool Conflicts(HotkeyGesture candidate, HotkeyGesture? other) => other is { } o && o == candidate;
}
