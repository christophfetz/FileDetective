using System.Diagnostics;
using FileDetective.Core;
using Xunit.Abstractions;

namespace FileDetective.Tests;

/// <summary>
/// Target from PLAN.md 6.5: a search over 1 million entries in under 50 ms on 4 cores.
/// The search is limited to 4 threads here to match the target. Locally in a Release build the 50 ms limit applies;
/// on CI runners (environment variable CI=true, set by GitHub Actions) and in Debug builds the limit is 500 ms,
/// because shared runners are slow and noisy and Debug code is not optimized.
/// Run only these tests with: dotnet test -c Release --filter Category=Performance
/// </summary>
[Trait("Category", "Performance")]
public class PerformanceTests(ITestOutputHelper output)
{
    private const int EntryCount = 1_000_000;
    private const double LocalLimitMs = 50;
    private const double CiLimitMs = 500;

    private static readonly string[] Words =
    [
        "angebot", "rechnung", "bericht", "protokoll", "vertrag", "projekt", "kunde", "müller", "schmidt",
        "entwurf", "präsentation", "notizen", "budget", "planung", "foto", "urlaub", "scan", "lieferschein",
        "bestellung", "archiv", "Übersicht", "konzept", "handbuch", "liste", "auswertung", "meier", "café",
    ];

    private static readonly string[] Extensions = [".pdf", ".docx", ".xlsx", ".txt", ".jpg", ".msg", ".pptx", ".zip"];

    [Fact]
    public void Search_OneMillionEntries_MeetsTarget()
    {
        bool ci = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

        // The target applies to the optimized program (Release). A Debug build runs the search without JIT
        // optimizations (measured about 5x slower), so plain "dotnet test" only checks the generous limit there.
        // The real measurement: dotnet test -c Release --filter Category=Performance
        bool optimized = !IsDebugBuild;
        double limit = ci || !optimized ? CiLimitMs : LocalLimitMs;

        using var fourCores = Build(4);
        using var allCores = Build(-1);

        string[] queries = ["angebot müller", "bericht 2024 .pdf", "e", "projekt", "\"liste 1\"", "xyzzy"];
        double worst4 = 0;
        foreach (var q in queries)
        {
            var (median4, total) = Measure(fourCores, q);
            var (medianAll, _) = Measure(allCores, q);
            worst4 = Math.Max(worst4, median4);
            output.WriteLine($"{q,-22} hits {total,9:N0}   4 threads {median4,6:F1} ms   {Environment.ProcessorCount} threads {medianAll,6:F1} ms");
        }

        output.WriteLine($"Worst median on 4 threads: {worst4:F1} ms (limit {limit} ms, logical processors {Environment.ProcessorCount})");
        Assert.True(worst4 < limit, $"Worst median {worst4:F1} ms exceeds {limit} ms.");
    }

#if DEBUG
    private const bool IsDebugBuild = true;
#else
    private const bool IsDebugBuild = false;
#endif

    private static (double MedianMs, int Total) Measure(MemoryIndex index, string input)
    {
        var query = SearchQuery.Parse(input);
        int total = 0;
        for (int i = 0; i < 3; i++)
        {
            total = index.Search(query).TotalCount; // warm-up
        }

        var times = new double[15];
        for (int i = 0; i < times.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            index.Search(query);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        Array.Sort(times);
        return (times[times.Length / 2], total);
    }

    /// <summary>
    /// 1 million entries: one root, 50 + 1,000 + 50,000 folders in three levels, the rest files spread over the
    /// deepest folders. Names are combinations of typical words, numbers and years. Fixed seed.
    /// </summary>
    private static MemoryIndex Build(int threads)
    {
        var index = new MemoryIndex(threads);
        var rnd = new Random(42);
        string Word() => Words[rnd.Next(Words.Length)];

        using var u = index.BeginUpdate();
        int id = 1;
        u.Set(id++, 0, 1, @"C:\Users\anna", true, 0);
        var level1 = new List<int>();
        for (int i = 0; i < 50; i++)
        {
            level1.Add(id);
            u.Set(id++, 1, 1, $"{Word()} {i}", true, rnd.Next(1_700_000_000, 1_800_000_000));
        }

        var level2 = new List<int>();
        foreach (var p in level1)
        {
            for (int i = 0; i < 20; i++)
            {
                level2.Add(id);
                u.Set(id++, p, 1, $"{Word()} {2000 + rnd.Next(27)}", true, rnd.Next(1_700_000_000, 1_800_000_000));
            }
        }

        var level3 = new List<int>();
        foreach (var p in level2)
        {
            for (int i = 0; i < 50; i++)
            {
                level3.Add(id);
                u.Set(id++, p, 1, $"{Word()}_{Word()}", true, rnd.Next(1_700_000_000, 1_800_000_000));
            }
        }

        while (id <= EntryCount)
        {
            int parent = level3[rnd.Next(level3.Count)];
            string name = $"{Word()} {Word()} {2000 + rnd.Next(27)}-{rnd.Next(100):D2}{Extensions[rnd.Next(Extensions.Length)]}";
            u.Set(id++, parent, 1, name, false, rnd.Next(1_700_000_000, 1_800_000_000));
        }

        return index;
    }
}
