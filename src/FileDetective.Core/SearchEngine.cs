namespace FileDetective.Core;

/// <summary>
/// Parses the input, searches the <see cref="MemoryIndex"/>, ranks, and resolves the best hits
/// to full paths through an <see cref="IEntryResolver"/> (PLAN.md 5.2, 6.4).
/// Thread-safe; several searches may run at the same time.
/// </summary>
public sealed class SearchEngine
{
    /// <summary>Number of hits returned (PLAN.md 6.4).</summary>
    public const int MaxResults = 100;

    private readonly MemoryIndex _index;
    private readonly IEntryResolver _resolver;
    private readonly IUsageLookup _usage;

    public SearchEngine(MemoryIndex index, IEntryResolver resolver, IUsageLookup? usage = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(resolver);
        _index = index;
        _resolver = resolver;
        _usage = usage ?? NoUsage.Instance;
    }

    /// <summary>Searches for <paramref name="input"/>. Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public SearchResult Search(string? input, SearchMode mode, CancellationToken cancellationToken = default)
        => Search(SearchQuery.Parse(input, mode), cancellationToken);

    /// <summary>
    /// The entries last opened through the tool, newest first, at most <paramref name="max"/> (PLAN.md 6.4: shown for an
    /// empty input). Only entries that still exist in the directory are returned, and only those that fit
    /// <paramref name="mode"/>. The hits carry no name level and quality.
    /// </summary>
    public SearchResult RecentlyOpened(SearchMode mode, int max = MaxResults, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (_usage is not IRecentUsage recent)
        {
            return SearchResult.Empty;
        }

        // Ids of vanished entries or of the other mode are skipped, so ask for more until enough hits are there or the
        // list is used up.
        int take = max * 2;
        while (true)
        {
            var ids = recent.GetMostRecent(take);
            var details = _resolver.Resolve(ids, cancellationToken);
            if (details.Count != ids.Count)
            {
                throw new InvalidOperationException("The entry resolver must return one element per requested id.");
            }

            var hits = new List<SearchHit>(Math.Min(max, ids.Count));
            for (int i = 0; i < ids.Count && hits.Count < max; i++)
            {
                if (details[i] is { } d && FitsMode(d, mode))
                {
                    hits.Add(new SearchHit(ids[i], d, 0, 0));
                }
            }

            if (hits.Count >= max || ids.Count < take)
            {
                return new SearchResult(hits, hits.Count);
            }

            take *= 4;
        }
    }

    private static bool FitsMode(EntryDetails d, SearchMode mode) => mode switch
    {
        SearchMode.Files => !d.IsDirectory,
        SearchMode.Folders => d.IsDirectory,
        SearchMode.Programs => !d.IsDirectory && MemoryIndex.IsProgramPath(d.FullPath),
        _ => true,
    };

    /// <summary>Searches for an already parsed query.</summary>
    public SearchResult Search(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.IsEmpty)
        {
            // The UI shows the recently opened entries for an empty input (stage 7), not the search.
            return SearchResult.Empty;
        }

        var result = _index.Search(query, _usage, MaxResults, cancellationToken);
        if (result.Matches.Count == 0)
        {
            return new SearchResult([], result.TotalCount);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Resolution happens outside the index lock, so slow database access never blocks writers.
        var ids = new int[result.Matches.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = result.Matches[i].EntryId;
        }

        var details = _resolver.Resolve(ids, cancellationToken);
        if (details.Count != ids.Length)
        {
            throw new InvalidOperationException("The entry resolver must return one element per requested id.");
        }

        var hits = new List<SearchHit>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            if (details[i] is { } d)
            {
                var m = result.Matches[i];
                hits.Add(new SearchHit(m.EntryId, d, m.NameLevel, m.NameQuality));
            }
        }

        // Decision: TotalCount stays the index count even if the resolver drops a vanished entry.
        return new SearchResult(hits, result.TotalCount);
    }
}
