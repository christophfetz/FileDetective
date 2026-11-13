using System.Diagnostics;
using FileDetective.Core;
using Xunit.Abstractions;

namespace FileDetective.Tests;

/// <summary>
/// Target from PLAN.md Etappe 2: loading 1 million entries from the database in under 3 seconds. Locally in a Release
/// build the 3 s limit applies; on CI (CI=true) and in Debug builds the limit is 15 s (shared runners are slow, Debug
/// code is not optimized). Run only these tests with: dotnet test -c Release --filter Category=Performance
/// </summary>
[Trait("Category", "Performance")]
public class StoragePerformanceTests(ITestOutputHelper output)
{
    private const int EntryCount = 1_000_000;
    private const double LocalLimitMs = 3000;
    private const double CiLimitMs = 15000;

    private static readonly string[] Words =
    [
        "angebot", "rechnung", "bericht", "protokoll", "vertrag", "projekt", "kunde", "müller", "schmidt",
        "entwurf", "präsentation", "notizen", "budget", "planung", "foto", "urlaub", "scan", "lieferschein",
    ];

    private static readonly string[] Extensions = [".pdf", ".docx", ".xlsx", ".txt", ".jpg", ".msg"];

    [Fact]
    public void LoadOneMillionEntries_MeetsTarget()
    {
        using var dir = new TempDir();
        {
            using var db = IndexDatabase.Open(dir.DbPath);
            using var store = new IndexStore(db, new MemoryIndex());
            long start = Stopwatch.GetTimestamp();
            Fill(store);
            output.WriteLine($"Filled database with {EntryCount:N0} entries in {Stopwatch.GetElapsedTime(start).TotalSeconds:F1} s");
        }

        bool ci = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        double limit = ci || IsDebugBuild ? CiLimitMs : LocalLimitMs;

        double best = double.MaxValue;
        for (int run = 0; run < 3; run++)
        {
            using var db = IndexDatabase.Open(dir.DbPath);
            using var index = new MemoryIndex();
            using var store = new IndexStore(db, index);
            long start = Stopwatch.GetTimestamp();
            int loaded = store.LoadIntoMemory();
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Assert.Equal(EntryCount, loaded);
            Assert.Equal(EntryCount, index.Count);
            output.WriteLine($"Load run {run + 1}: {ms:F0} ms ({loaded / ms * 1000:N0} entries/s)");
            best = Math.Min(best, ms);
            if (run == 0)
            {
                // Sanity: the loaded index finds something and resolves it through the database.
                var hits = new SearchEngine(index, new DatabaseEntryResolver(db)).Search("angebot müller .pdf", SearchMode.All);
                Assert.NotEmpty(hits.Hits);
            }
        }

        output.WriteLine($"Best load time {best:F0} ms (limit {limit:F0} ms)");
        Assert.True(best < limit, $"Loading took {best:F0} ms, limit {limit:F0} ms.");
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private static void Fill(IndexStore store)
    {
        var rnd = new Random(7);
        string Word() => Words[rnd.Next(Words.Length)];

        var root = store.AddRoot(@"C:\Users\anna", null, RootKind.Local);
        var folders = new List<int> { root.RootEntryId };
        var batch = new ChangeBatch();
        int total = 1;

        void Flush()
        {
            store.Apply(root.Id, batch);
            batch = new ChangeBatch();
        }

        // 5,000 folders in two levels first, then files below random folders.
        for (int i = 0; i < 50; i++)
        {
            int id = store.AllocateId();
            batch.Inserts.Add(new EntryInsert(id, root.RootEntryId, $"{Word()} {i}", true, 0, 1_700_000_000 + i, FileAttributes.Directory));
            folders.Add(id);
            total++;
            for (int j = 0; j < 100; j++)
            {
                int sub = store.AllocateId();
                batch.Inserts.Add(new EntryInsert(sub, id, $"{Word()} {2000 + j % 27}", true, 0, 1_700_000_000 + j, FileAttributes.Directory));
                folders.Add(sub);
                total++;
            }
        }

        Flush();
        while (total < EntryCount)
        {
            int id = store.AllocateId();
            string name = $"{Word()} {Word()} {2000 + rnd.Next(27)}-{rnd.Next(100):D2}{Extensions[rnd.Next(Extensions.Length)]}";
            batch.Inserts.Add(new EntryInsert(
                id, folders[rnd.Next(folders.Count)], name, false, rnd.Next(1, 10_000_000), rnd.Next(1_700_000_000, 1_800_000_000), FileAttributes.Archive));
            total++;
            if (batch.Count >= 50_000)
            {
                Flush();
            }
        }

        Flush();
    }
}
