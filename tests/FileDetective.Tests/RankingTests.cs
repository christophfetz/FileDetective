using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>
/// Ranking (PLAN.md 6.4, "Rangfolge"). Each test is built so that exactly the criterion under test decides:
/// all later criteria (and the id) point the other way.
/// </summary>
public class RankingTests
{
    [Fact]
    public void Criterion1_NameLevel()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.Dir(2, 1, "bericht");
        t.Dir(3, 1, "archiv");
        t.Dir(4, 1, "bericht 2024");
        t.File(10, 4, "aa.txt", mtime: 500);              // level 0: both terms only in the path
        t.File(11, 2, "2024 notizen.txt", mtime: 300);    // level 1: "2024" in the name, "bericht" in the path
        t.File(12, 3, "bericht 2024 lang.pdf", mtime: 100); // level 2

        var hits = t.Search("bericht 2024", SearchMode.Files).Hits;
        Assert.Equal([12, 11, 10], hits.Select(h => h.EntryId));
        Assert.Equal([2, 1, 0], hits.Select(h => h.NameLevel));
    }

    [Fact]
    public void Criterion2_NameQuality()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(10, 1, "bericht.docx", mtime: 1); // 3: name without extension is exactly the input
        t.File(11, 1, "berichte.a", mtime: 2);   // 2: starts with the first term
        t.File(12, 1, "x bericht", mtime: 3);    // 1: term starts at a word boundary
        t.File(13, 1, "xbericht", mtime: 4);     // 0

        var hits = t.Search("bericht", SearchMode.Files).Hits;
        Assert.Equal([10, 11, 12, 13], hits.Select(h => h.EntryId));
        Assert.Equal([3, 2, 1, 0], hits.Select(h => h.NameQuality));
    }

    [Fact]
    public void Criterion2_ExactMatchWithSeveralTerms()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(10, 1, "Angebot Müller.pdf", mtime: 1);
        t.File(11, 1, "angebot mueller x", mtime: 2);
        var hits = t.Search("angebot müller", SearchMode.Files).Hits;
        Assert.Equal([10, 11], hits.Select(h => h.EntryId));
        Assert.Equal(3, hits[0].NameQuality);
    }

    [Fact]
    public void Criterion2_StartsWithFirstTerm_NotAnyTerm()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(10, 1, "zz angebot 1.txt", mtime: 1); // starts with the first term
        t.File(11, 1, "angebot zz.tx", mtime: 2);  // starts with the second term only: word boundary
        var hits = t.Search("zz angebot", SearchMode.Files).Hits;
        Assert.Equal([10, 11], hits.Select(h => h.EntryId));
        Assert.Equal([2, 1], hits.Select(h => h.NameQuality));
    }

    [Fact]
    public void Criterion3_OpenedThroughTool()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(10, 1, "plan a.txt", mtime: 9);   // opened earlier
        t.File(11, 1, "plan b.txt", mtime: 1);   // opened most recently
        t.File(12, 1, "plan c.txt", mtime: 99);  // never opened
        var usage = new DictionaryUsage(new() { [10] = 50, [11] = 100 });

        var hits = t.Search("plan", SearchMode.Files, usage).Hits;
        Assert.Equal([11, 10, 12], hits.Select(h => h.EntryId));
    }

    [Fact]
    public void Criterion4_LastWriteTime()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(10, 1, "plan a.txt", mtime: 5);
        t.File(11, 1, "plan longer name.txt", mtime: 10);
        Assert.Equal([11, 10], t.Search("plan", SearchMode.Files).Hits.Select(h => h.EntryId));
    }

    [Fact]
    public void Criterion5_ShorterNameFirst()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(10, 1, "plan aaaa.txt", mtime: 5);
        t.File(11, 1, "plan zz.txt", mtime: 5);
        Assert.Equal([11, 10], t.Search("plan", SearchMode.Files).Hits.Select(h => h.EntryId));
    }

    [Fact]
    public void Criterion5_ThenAlphabetical()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(10, 1, "plan b.txt", mtime: 5);
        t.File(11, 1, "plan a.txt", mtime: 5);
        Assert.Equal([11, 10], t.Search("plan", SearchMode.Files).Hits.Select(h => h.EntryId));
    }

    [Fact]
    public void FinalTieBreak_IsEntryId()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.Dir(2, 1, "x");
        t.Dir(3, 1, "y");
        t.File(11, 2, "plan.txt", mtime: 5);
        t.File(10, 3, "plan.txt", mtime: 5);
        Assert.Equal([10, 11], t.Search("plan", SearchMode.Files).Hits.Select(h => h.EntryId));
    }

    [Fact]
    public void SameIndexAndInput_GiveSameOrder_RegardlessOfThreads()
    {
        // Many exact ties (identical names and times) in several blocks; only the id separates them.
        using var many = new MemoryIndex();
        using var single = new MemoryIndex(maxDegreeOfParallelism: 1);
        foreach (var index in new[] { many, single })
        {
            using var u = index.BeginUpdate();
            u.Set(1, 0, 1, @"C:\r", true, 0);
            var r = new Random(7);
            for (int id = 2; id < 300_000; id++)
            {
                u.Set(id, 1, 1, $"report {r.Next(5)}.txt", false, r.Next(3));
            }
        }

        var query = SearchQuery.Parse("report");
        var expected = single.Search(query).Matches;
        Assert.Equal(SearchEngine.MaxResults, expected.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(expected, many.Search(query).Matches);
        }
    }

    [Fact]
    public void BoundedBestLists_GiveTheTrueTopResults()
    {
        // With a result size larger than all matches, nothing is cut; the top 100 must be its prefix.
        using var index = new MemoryIndex();
        using (var u = index.BeginUpdate())
        {
            u.Set(1, 0, 1, @"C:\r", true, 0);
            var r = new Random(11);
            for (int id = 2; id < 200_000; id++)
            {
                string name = r.Next(4) switch
                {
                    0 => $"report {r.Next(1000)}.txt",
                    1 => $"xreport{r.Next(100)}.pdf",
                    2 => "report",
                    _ => $"other {r.Next(1000)}.txt",
                };
                u.Set(id, 1, 1, name, false, r.Next(50));
            }
        }

        var usage = new DictionaryUsage(Enumerable.Range(0, 100).ToDictionary(i => (i * 997) + 2, i => (long)i % 7));
        var query = SearchQuery.Parse("report");
        var all = index.Search(query, usage, maxResults: 200_000);
        var top = index.Search(query, usage);
        Assert.Equal(all.TotalCount, top.TotalCount);
        Assert.Equal(all.TotalCount, all.Matches.Count);
        Assert.Equal(all.Matches.Take(SearchEngine.MaxResults), top.Matches);
    }
}
