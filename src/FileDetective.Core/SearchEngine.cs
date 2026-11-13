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
