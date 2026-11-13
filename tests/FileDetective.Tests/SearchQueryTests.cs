using FileDetective.Core;

namespace FileDetective.Tests;

public class SearchQueryTests
{
    [Fact]
    public void SplitsAtWhitespace_AndNormalizes()
    {
        var q = SearchQuery.Parse("  Angebot   MÜLLER\t2026 ");
        Assert.Equal(["angebot", "mueller", "2026"], q.Terms);
        Assert.Empty(q.TypeFilters);
    }

    [Fact]
    public void QuotedText_StaysOneTerm()
    {
        var q = SearchQuery.Parse("\"Angebot Müller\" 2026");
        Assert.Equal(["angebot mueller", "2026"], q.Terms);
    }

    [Fact]
    public void UnterminatedQuote_RunsToEnd()
    {
        var q = SearchQuery.Parse("x \"a b");
        Assert.Equal(["x", "a b"], q.Terms);
    }

    [Fact]
    public void Separators_SplitTerms()
    {
        var q = SearchQuery.Parse(@"kunden\müller/2026 \\x");
        Assert.Equal(["kunden", "mueller", "2026", "x"], q.Terms);
    }

    [Fact]
    public void TypeFilters_AreRecognized()
    {
        var q = SearchQuery.Parse("angebot .PDF .docx .a1 . .tar.gz .abcdefghijk \".pdf\"");
        Assert.Equal([".pdf", ".docx", ".a1"], q.TypeFilters);
        // "." alone, ".tar.gz", more than 10 characters and quoted text are ordinary terms.
        Assert.Equal(["angebot", ".", ".tar.gz", ".abcdefghijk", ".pdf"], q.Terms);
    }

    [Fact]
    public void MoreThanEightTerms_AreIgnored()
    {
        var q = SearchQuery.Parse("a b c d e f g h i j");
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g", "h"], q.Terms);
    }

    [Fact]
    public void Empty_HasNoTerms()
    {
        Assert.True(SearchQuery.Parse("").IsEmpty);
        Assert.True(SearchQuery.Parse("   \"\" / \\ ").IsEmpty);
        Assert.True(SearchQuery.Parse(null).IsEmpty);
        Assert.False(SearchQuery.Parse(".pdf").IsEmpty);
    }

    [Fact]
    public void Mode_IsKept()
    {
        Assert.Equal(SearchMode.Folders, SearchQuery.Parse("x", SearchMode.Folders).Mode);
    }
}
