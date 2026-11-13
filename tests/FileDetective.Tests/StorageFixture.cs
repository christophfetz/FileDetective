using FileDetective.Core;
using Microsoft.Data.Sqlite;

namespace FileDetective.Tests;

/// <summary>A temp folder that is removed on dispose; releases pooled SQLite connections first.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FileDetectiveTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string DbPath => System.IO.Path.Combine(Path, "index.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
            // best effort; the temp folder is not important enough to fail a test
        }
    }
}

/// <summary>Database + store + memory index in a temp folder; can be closed and reopened like a program restart.</summary>
internal sealed class StorageFixture : IDisposable
{
    private readonly TempDir _dir;

    public StorageFixture(TempDir dir)
    {
        _dir = dir;
        Open();
    }

    public IndexDatabase Db { get; private set; } = null!;

    public MemoryIndex Index { get; private set; } = null!;

    public IndexStore Store { get; private set; } = null!;

    public UsageTracker Usage { get; private set; } = null!;

    public SearchEngine Engine { get; private set; } = null!;

    /// <summary>Closes everything and opens it again from the file; the memory index is empty until <see cref="Load"/>.</summary>
    public void Restart()
    {
        Close();
        Open();
    }

    public int Load()
    {
        int n = Store.LoadIntoMemory();
        Usage.Reload();
        return n;
    }

    /// <summary>Adds one entry in its own batch and returns its number.</summary>
    public int Add(RootInfo root, int parent, string name, bool isDir, long mtime = 0, long size = 0)
    {
        int id = Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(id, parent, name, isDir, size, mtime, isDir ? FileAttributes.Directory : FileAttributes.Normal));
        Store.Apply(root.Id, batch);
        return id;
    }

    public string[] Paths(string input, SearchMode mode = SearchMode.All)
        => [.. Engine.Search(input, mode).Hits.Select(h => h.Details.FullPath)];

    public void Dispose() => Close();

    private void Open()
    {
        Db = IndexDatabase.Open(_dir.DbPath);
        Index = new MemoryIndex();
        Store = new IndexStore(Db, Index);
        Usage = new UsageTracker(Db, Store);
        Engine = new SearchEngine(Index, new DatabaseEntryResolver(Db), Usage);
    }

    private void Close()
    {
        Store?.Dispose();
        Index?.Dispose();
        Db?.Dispose();
    }
}
