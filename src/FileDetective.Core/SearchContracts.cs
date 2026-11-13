namespace FileDetective.Core;

/// <summary>
/// Tells the ranking which entries were opened through the tool (PLAN.md 6.4, criterion 3).
/// Implementations must be thread-safe for concurrent reads: the search calls this from several
/// threads while it holds the read lock of the <see cref="MemoryIndex"/>, so it must not write to the index.
/// </summary>
public interface IUsageLookup
{
    /// <summary>Returns true and the last opening time (Unix seconds, UTC) if the entry was opened through the tool.</summary>
    bool TryGetLastOpened(int entryId, out long lastOpenedUnixSeconds);
}

/// <summary>Usage lookup without any data: nothing was ever opened.</summary>
public sealed class NoUsage : IUsageLookup
{
    public static NoUsage Instance { get; } = new();

    private NoUsage()
    {
    }

    public bool TryGetLastOpened(int entryId, out long lastOpenedUnixSeconds)
    {
        lastOpenedUnixSeconds = 0;
        return false;
    }
}

/// <summary>Display data of one entry, resolved outside the in-memory index (database from stage 2 on).</summary>
public sealed record EntryDetails(
    string FullPath, string Name, bool IsDirectory, long Size, DateTime LastWriteUtc, FileAttributes Attributes);

/// <summary>Resolves entry numbers to full path and display data.</summary>
public interface IEntryResolver
{
    /// <summary>
    /// Returns one element per requested id, in the same order. <c>null</c> marks an entry that no longer exists;
    /// it is left out of the hits.
    /// </summary>
    IReadOnlyList<EntryDetails?> Resolve(IReadOnlyList<int> entryIds, CancellationToken cancellationToken);
}

/// <summary>One ranked match from the <see cref="MemoryIndex"/>.</summary>
/// <param name="EntryId">Entry number.</param>
/// <param name="NameLevel">2 = all terms in the own name, 1 = at least one, 0 = only in the path.</param>
/// <param name="NameQuality">Only for level 2: 3 exact, 2 starts with first term, 1 word boundary, 0 otherwise.</param>
public readonly record struct IndexMatch(int EntryId, int NameLevel, int NameQuality);

/// <summary>The best matches in rank order and the total number of matches.</summary>
public sealed record IndexSearchResult(IReadOnlyList<IndexMatch> Matches, int TotalCount)
{
    public static IndexSearchResult Empty { get; } = new([], 0);
}

/// <summary>One displayed hit.</summary>
public sealed record SearchHit(int EntryId, EntryDetails Details, int NameLevel, int NameQuality);

/// <summary>The hits in rank order and the total number of matches in the index.</summary>
public sealed record SearchResult(IReadOnlyList<SearchHit> Hits, int TotalCount)
{
    public static SearchResult Empty { get; } = new([], 0);
}
