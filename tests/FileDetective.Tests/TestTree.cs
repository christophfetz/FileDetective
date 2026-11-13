using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>
/// A <see cref="MemoryIndex"/> plus the original names, acting as the <see cref="IEntryResolver"/> that the
/// database provides from stage 2 on. Not thread-safe; for concurrency tests use the index directly.
/// </summary>
internal sealed class TestTree : IEntryResolver, IDisposable
{
    private readonly Dictionary<int, Node> _nodes = [];

    public TestTree(int maxDegreeOfParallelism = -1)
    {
        Index = new MemoryIndex(maxDegreeOfParallelism);
    }

    public MemoryIndex Index { get; }

    public int Root(int id, string path, long mtime = 0) => Add(id, 0, path, true, mtime);

    public int Dir(int id, int parent, string name, long mtime = 0) => Add(id, parent, name, true, mtime);

    public int File(int id, int parent, string name, long mtime = 0) => Add(id, parent, name, false, mtime);

    public void Rename(int id, string name)
    {
        _nodes[id] = _nodes[id] with { Name = name };
        Assert.True(Index.Rename(id, name));
    }

    public void Move(int id, int parent)
    {
        _nodes[id] = _nodes[id] with { Parent = parent };
        Assert.True(Index.Move(id, parent));
    }

    public void Delete(int id)
    {
        _nodes.Remove(id);
        Assert.True(Index.Delete(id));
    }

    public SearchResult Search(string input, SearchMode mode = SearchMode.All, IUsageLookup? usage = null)
        => new SearchEngine(Index, this, usage).Search(input, mode);

    public string[] Names(string input, SearchMode mode = SearchMode.All, IUsageLookup? usage = null)
        => [.. Search(input, mode, usage).Hits.Select(h => h.Details.Name)];

    public string[] Paths(string input, SearchMode mode = SearchMode.All)
        => [.. Search(input, mode).Hits.Select(h => h.Details.FullPath)];

    public IReadOnlyList<EntryDetails?> Resolve(IReadOnlyList<int> entryIds, CancellationToken cancellationToken)
    {
        var result = new EntryDetails?[entryIds.Count];
        for (int i = 0; i < result.Length; i++)
        {
            if (_nodes.TryGetValue(entryIds[i], out var n))
            {
                result[i] = new EntryDetails(PathOf(entryIds[i]), n.Name, n.IsDir, 0,
                    DateTime.UnixEpoch.AddSeconds(n.MTime), n.IsDir ? FileAttributes.Directory : FileAttributes.Normal);
            }
        }

        return result;
    }

    public string PathOf(int id)
    {
        var parts = new List<string>();
        for (int cur = id; cur != 0 && parts.Count <= _nodes.Count; cur = _nodes[cur].Parent)
        {
            parts.Add(_nodes[cur].Name);
        }

        parts.Reverse();
        return string.Join('\\', parts);
    }

    public void Dispose() => Index.Dispose();

    private int Add(int id, int parent, string name, bool isDir, long mtime)
    {
        _nodes[id] = new Node(parent, name, isDir, mtime);
        Index.Set(id, parent, 1, name, isDir, mtime);
        return id;
    }

    private sealed record Node(int Parent, string Name, bool IsDir, long MTime);
}

internal sealed class DictionaryUsage(Dictionary<int, long> opened) : IUsageLookup
{
    public bool TryGetLastOpened(int entryId, out long lastOpenedUnixSeconds)
        => opened.TryGetValue(entryId, out lastOpenedUnixSeconds);
}
