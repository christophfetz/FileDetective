using System.Runtime.CompilerServices;
using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Hit determination (PLAN.md 6.4, "Treffer bestimmen") and index changes.</summary>
public class MatchingTests
{
    private static TestTree Sample()
    {
        var t = new TestTree();
        t.Root(1, @"C:\Data");
        t.Dir(2, 1, "Kunden");
        t.Dir(3, 2, "Müller GmbH");
        t.File(4, 3, "Angebot 2026.pdf");
        t.File(5, 3, "Angebot 2026.docx");
        t.File(6, 3, "Angebot 2026.txt");
        t.Dir(7, 1, "Projekt Alpha");
        t.File(8, 7, "notes.txt");
        t.File(9, 1, "Mueller Angebot.pdf");
        return t;
    }

    [Fact]
    public void EntryRec_Is16Bytes() => Assert.Equal(16, Unsafe.SizeOf<EntryRec>());

    [Fact]
    public void WordOrder_DoesNotMatter()
    {
        using var t = Sample();
        var a = t.Names("angebot müller");
        var b = t.Names("müller angebot");
        Assert.Equal(4, a.Length);
        Assert.Equal(a.Order(), b.Order());
    }

    [Fact]
    public void TermOnlyInPath_Matches()
    {
        using var t = Sample();
        Assert.Equal(["notes.txt"], t.Names("alpha notes"));
        Assert.Equal(["notes.txt", "Projekt Alpha"], t.Names("alpha").Order());
    }

    [Fact]
    public void TermInRootPath_Matches()
    {
        using var t = Sample();
        Assert.Contains("notes.txt", t.Names("data notes"));
    }

    [Fact]
    public void AllTermsMustOccur()
    {
        using var t = Sample();
        Assert.Empty(t.Names("alpha angebot"));
    }

    [Fact]
    public void CaseAndUmlauts_DoNotMatter()
    {
        using var t = Sample();
        Assert.Equal(t.Names("müller").Order(), t.Names("MUELLER").Order());
        Assert.Contains("Mueller Angebot.pdf", t.Names("Müller"));
    }

    [Fact]
    public void QuotedText_MustOccurAsIs()
    {
        using var t = Sample();
        t.File(10, 1, "Angebot Müller.pdf");
        Assert.Equal(["Angebot Müller.pdf"], t.Names("\"angebot müller\""));
        Assert.Contains("Mueller Angebot.pdf", t.Names("angebot müller"));
    }

    [Fact]
    public void PathSeparators_SplitTerms()
    {
        using var t = Sample();
        Assert.Equal(3, t.Names(@"kunden\müller\angebot").Length);
        Assert.Equal(3, t.Names("kunden/müller angebot").Length);
    }

    [Fact]
    public void TypeFilter_MatchesNameEnd_SeveralAreOr()
    {
        using var t = Sample();
        Assert.Equal(["Angebot 2026.pdf", "Mueller Angebot.pdf"], t.Names("angebot .pdf").Order());
        Assert.Equal(["Angebot 2026.docx", "Angebot 2026.pdf", "Mueller Angebot.pdf"], t.Names("angebot .pdf .DOCX").Order());
        Assert.Equal(["Angebot 2026.pdf", "Mueller Angebot.pdf"], t.Names(".pdf").Order());
    }

    [Fact]
    public void Mode_FiltersFilesAndFolders()
    {
        using var t = Sample();
        Assert.Equal(["notes.txt", "Projekt Alpha"], t.Names("alpha", SearchMode.All).Order());
        Assert.Equal(["notes.txt"], t.Names("alpha", SearchMode.Files));
        Assert.Equal(["Projekt Alpha"], t.Names("alpha", SearchMode.Folders));
    }

    [Fact]
    public void MoreThanEightTerms_NinthIsIgnored()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(2, 1, "a b c d e f g h.txt");
        Assert.Equal(["a b c d e f g h.txt"], t.Names("a b c d e f g h zzz-not-there"));
        Assert.Empty(t.Names("zzz-not-there a b c d e f g h"));
    }

    [Fact]
    public void EmptyInput_HasNoHits()
    {
        using var t = Sample();
        var r = t.Search("   ");
        Assert.Empty(r.Hits);
        Assert.Equal(0, r.TotalCount);
    }

    [Fact]
    public void FullPath_ComesFromResolver()
    {
        using var t = Sample();
        Assert.Equal([@"C:\Data\Projekt Alpha\notes.txt"], t.Paths("notes"));
    }

    [Fact]
    public void TotalCount_CountsAllMatches_HitsAreCapped()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        for (int i = 0; i < 250; i++)
        {
            t.File(10 + i, 1, $"file {i}.txt");
        }

        var r = t.Search("file");
        Assert.Equal(250, r.TotalCount);
        Assert.Equal(SearchEngine.MaxResults, r.Hits.Count);
    }

    [Fact]
    public void DeletedEntries_DoNotAppear()
    {
        using var t = Sample();
        t.Delete(8);
        Assert.Empty(t.Names("notes"));
        Assert.Equal(["Projekt Alpha"], t.Names("alpha"));
    }

    [Fact]
    public void DeletedFolder_HidesItsSubtree()
    {
        using var t = Sample();
        t.Delete(3);
        Assert.Equal(["Mueller Angebot.pdf"], t.Names("angebot"));
    }

    [Fact]
    public void RenamedFolder_ChangesPathHitsOfChildren()
    {
        using var t = Sample();
        Assert.Single(t.Names("alpha notes"));
        t.Rename(7, "Projekt Beta");
        Assert.Empty(t.Names("alpha notes"));
        Assert.Equal(["notes.txt"], t.Names("beta notes"));
        Assert.Equal([@"C:\Data\Projekt Beta\notes.txt"], t.Paths("beta notes"));
    }

    [Fact]
    public void MovedFolder_ChangesPathHitsOfChildren()
    {
        using var t = Sample();
        t.Move(7, 3);
        Assert.Equal(["notes.txt"], t.Names("müller notes"));
    }

    [Fact]
    public void ParentWithHigherIdThanChild_Works()
    {
        using var t = new TestTree();
        t.File(1, 500, "plan.txt");
        t.Dir(500, 900, "Inner");
        t.Dir(900, 70000, "Outer"); // in another block
        t.Root(70000, @"D:\Top");
        Assert.Equal(["plan.txt"], t.Names("top outer inner plan"));
        Assert.Equal([@"D:\Top\Outer\Inner\plan.txt"], t.Paths("plan"));
    }

    [Fact]
    public void EntryWithMissingParent_DoesNotAppear()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(2, 1, "found.txt");
        t.File(3, 99, "orphan found.txt");
        Assert.Equal(["found.txt"], t.Names("found"));
    }

    [Fact]
    public void ParentCycle_DoesNotHang_AndHidesTheCycle()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.Dir(2, 1, "a");
        t.Dir(3, 2, "b");
        t.File(4, 3, "x.txt");
        t.Move(2, 3); // a -> b -> a
        Assert.Empty(t.Names("x"));
        Assert.Equal([@"C:\r"], t.Names("r"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeepPath_DoesNotOverflowTheStack(bool descendingIds)
    {
        const int depth = 100_000;
        using var index = new MemoryIndex();
        using (var u = index.BeginUpdate())
        {
            // Root is either the lowest or the highest id; each folder sits below the previous one.
            int Id(int level) => descendingIds ? depth + 1 - level : level + 1;
            u.Set(Id(0), 0, 1, @"C:\deep-root", true, 0);
            for (int level = 1; level < depth; level++)
            {
                u.Set(Id(level), Id(level - 1), 1, "d" + level, true, 0);
            }

            u.Set(depth + 1 + 1, Id(depth - 1), 1, "bottom.txt", false, 0);
        }

        var r = index.Search(SearchQuery.Parse("deep-root bottom"));
        Assert.Equal(1, r.TotalCount);
        Assert.Equal(depth + 2, r.Matches[0].EntryId);
    }

    [Fact]
    public void SetOnExistingId_ReplacesEntry()
    {
        using var t = new TestTree();
        t.Root(1, @"C:\r");
        t.File(2, 1, "old.txt");
        t.File(2, 1, "new.txt");
        Assert.Empty(t.Names("old"));
        Assert.Equal(["new.txt"], t.Names("new"));
        Assert.Equal(2, t.Index.Count);
    }

    [Fact]
    public void Changes_OnMissingIds_ReturnFalse()
    {
        using var index = new MemoryIndex();
        Assert.False(index.Delete(5));
        Assert.False(index.Rename(5, "x"));
        Assert.False(index.Move(5, 1));
        index.Set(5, 0, 1, "r", true, 0);
        Assert.True(index.Delete(5));
        Assert.False(index.Delete(5));
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void ManyNames_SpanSeveralSegments()
    {
        // 16 MiB segments: 140,000 names of 250 bytes need three segments.
        using var index = new MemoryIndex();
        string pad = new('p', 240);
        using (var u = index.BeginUpdate())
        {
            u.Set(1, 0, 1, "root", true, 0);
            for (int i = 2; i < 140_000; i++)
            {
                u.Set(i, 1, 1, $"{pad}{i:D8}", false, 0);
            }
        }

        var r = index.Search(SearchQuery.Parse("00139999"));
        Assert.Equal(1, r.TotalCount);
        Assert.Equal(139_999, r.Matches[0].EntryId);
        r = index.Search(SearchQuery.Parse("00000002"));
        Assert.Equal(2, r.Matches[0].EntryId);
    }
}
