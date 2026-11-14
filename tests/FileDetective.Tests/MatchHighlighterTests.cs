using FileDetective.Core;

namespace FileDetective.Tests;

public class MatchHighlighterTests
{
    private static string[] Marked(string name, string input)
    {
        var query = SearchQuery.Parse(input);
        return [.. MatchHighlighter.Find(name, query).Select(r => name.Substring(r.Start, r.Length))];
    }

    private static string[] Marked(string name, params byte[][] terms)
        => [.. MatchHighlighter.Find(name, terms).Select(r => name.Substring(r.Start, r.Length))];

    [Fact]
    public void Ascii_MarksEveryOccurrenceOfEveryTerm_CaseInsensitive()
    {
        Assert.Equal(["Angebot", "Mueller"], Marked("Angebot_Mueller_2026.pdf", "mueller angebot"));
        Assert.Equal(["a", "a", "a", "A"], Marked("banana_A", "a").Where(s => s.Length == 1).ToArray());
        var ranges = MatchHighlighter.Find("aaa", SearchQuery.Parse("aa"));
        Assert.Single(ranges); // overlapping hits are merged
        Assert.Equal(new MatchRange(0, 3), ranges[0]);
    }

    [Fact]
    public void Ascii_ExactRanges()
    {
        var ranges = MatchHighlighter.Find("Report Final report", SearchQuery.Parse("report"));
        Assert.Equal([new MatchRange(0, 6), new MatchRange(13, 6)], ranges);
    }

    [Fact]
    public void NoTermsOrNoHit_GivesNothing()
    {
        Assert.Empty(MatchHighlighter.Find("abc", SearchQuery.Parse("")));
        Assert.Empty(MatchHighlighter.Find("abc", SearchQuery.Parse(".pdf")));
        Assert.Empty(MatchHighlighter.Find("abc", SearchQuery.Parse("xyz")));
        Assert.Empty(MatchHighlighter.Find("", SearchQuery.Parse("a")));
    }

    [Theory]
    [InlineData("Müller", "mueller", "Müller")]
    [InlineData("Müller", "müller", "Müller")]
    [InlineData("Müller", "mue", "Mü")]
    [InlineData("Müller", "ller", "ller")]
    [InlineData("Müller", "ue", "ü")]
    [InlineData("Müller", "u", "ü")] // starts inside the element: the whole element is marked
    [InlineData("Müller", "e", "ü|e")]
    [InlineData("Müller", "m", "M")]
    [InlineData("Mueller", "müller", "Mueller")]
    [InlineData("Straße", "strasse", "Straße")]
    [InlineData("Straße", "ss", "ß")]
    [InlineData("Straße", "s", "S|ß")]
    [InlineData("Straße", "asse", "aße")]
    [InlineData("Größe 5", "groesse", "Größe")]
    [InlineData("Café Olé", "cafe ole", "Café|Olé")]
    [InlineData("Café", "e", "é")]
    [InlineData("Crème brûlée", "brulee", "brûlée")]
    [InlineData("ÄÖÜ", "aeoeue", "ÄÖÜ")]
    [InlineData("ÄÖÜ", "oe", "Ö")]
    [InlineData("Ärger Öl Übung", "oel ueb", "Öl|Üb")]
    public void Composed_MapsSearchFormPositionsToOriginalCharacters(string name, string input, string expected)
    {
        Assert.Equal(expected.Split('|'), Marked(name, input));
    }

    [Theory]
    [InlineData("Müller", "mueller", "Müller")] // decomposed ü
    [InlineData("Müller", "mue", "Mü")]
    [InlineData("Müller", "ller", "ller")]
    [InlineData("Müller", "ue", "ü")]
    [InlineData("Müller", "m l", "M|ll")] // two terms; touching ranges are merged
    [InlineData("Café", "cafe", "Café")] // e + combining acute
    [InlineData("Café", "e", "é")]
    [InlineData("Café au lait", "lait", "lait")]
    [InlineData("ÅB", "ab", "ÅB")] // A + ring above (Å decomposed): the ring is dropped in the search form
    [InlineData("Straße", "ss", "ß")]
    [InlineData("öl ö", "oel", "öl")]
    public void Decomposed_MapsToTheWholeTextElement(string name, string input, string expected)
    {
        Assert.Equal(expected.Split('|'), Marked(name, input));
    }

    [Fact]
    public void Emoji_And_SurrogatePairs_AreNeverCutInHalf()
    {
        string name = "Foto \U0001F600 Müller";
        var ranges = MatchHighlighter.Find(name, SearchQuery.Parse("müller"));
        Assert.Single(ranges);
        Assert.Equal("Müller", name.Substring(ranges[0].Start, ranges[0].Length));

        // A hit on the text before an emoji ends before the surrogate pair.
        ranges = MatchHighlighter.Find(name, SearchQuery.Parse("foto"));
        Assert.Equal(new MatchRange(0, 4), ranges[0]);
    }

    [Fact]
    public void LoneSurrogate_FallsBackToWholeNameWithoutThrowing()
    {
        string name = "ab\uD800cd";
        var ranges = MatchHighlighter.Find(name, SearchQuery.Parse("cd"));
        // Nothing may throw; if a range is given it must lie inside the name.
        Assert.All(ranges, r => Assert.InRange(r.End, 1, name.Length));
    }

    [Fact]
    public void MultipleTermsOverlappingAcrossNormalization_AreMerged()
    {
        // "mue" and "ueller" overlap in the search form (m-ue-ller); both map to ranges inside "Müller".
        var ranges = MatchHighlighter.Find("Müller", SearchQuery.Parse("mue ueller"));
        Assert.Equal([new MatchRange(0, 6)], ranges);
    }

    [Fact]
    public void TermsAsBytes_MatchTheQueryOverload()
    {
        var q = SearchQuery.Parse("müller");
        Assert.Equal(
            MatchHighlighter.Find("Hr. Müller", q),
            MatchHighlighter.Find("Hr. Müller", [SearchNormalizer.Normalize("müller")]));
        Assert.Equal(["Müller"], Marked("Hr. Müller", SearchNormalizer.Normalize("MUELLER")));
    }

    [Fact]
    public void Consistency_WithTheIndex_EveryTermThatMatchesTheIndexIsMarked()
    {
        // For a mix of names: whenever the search form of the name contains the term, at least one range is marked,
        // every range lies inside the name, ranges are sorted and do not overlap.
        string[] names =
        [
            "Müller", "MÜLLER", "Mueller", "Müller", "Straße", "STRASSE", "Café", "Café", "naïve", "Ångström",
            "Åsa", "straße-ß-ss", "日本語のファイル.txt", "Ünïcödé", "x́y", "ǅ", "Ⅷ", "ạ̈",
        ];
        string[] terms = ["m", "mue", "ue", "ss", "e", "ae", "oe", "an", "angstroem", "angstrom", "ss-", "ly", "txt", "x"];
        foreach (string name in names)
        {
            byte[] form = SearchNormalizer.Normalize(name);
            foreach (string term in terms)
            {
                byte[] t = SearchNormalizer.Normalize(term);
                var ranges = MatchHighlighter.Find(name, [t]);
                bool inIndex = form.AsSpan().IndexOf(t) >= 0;
                if (inIndex)
                {
                    Assert.NotEmpty(ranges);
                }
                else
                {
                    Assert.Empty(ranges);
                }

                int previousEnd = -1;
                foreach (var r in ranges)
                {
                    Assert.True(r.Start >= 0 && r.Length > 0 && r.End <= name.Length, $"{name}/{term}: {r}");
                    Assert.True(r.Start > previousEnd, $"{name}/{term}: overlap");
                    previousEnd = r.End;
                }
            }
        }
    }

    [Fact]
    public void RandomNames_NeverThrow_AndStayInsideTheName()
    {
        var rnd = new Random(7);
        string alphabet = "aAbBüÜöäßé̈́ -_.sSeE\U0001F600İıΣσ";
        var chars = alphabet.EnumerateRunes().ToArray();
        for (int n = 0; n < 2000; n++)
        {
            var sb = new System.Text.StringBuilder();
            int len = rnd.Next(1, 12);
            for (int i = 0; i < len; i++)
            {
                sb.Append(chars[rnd.Next(chars.Length)].ToString());
            }

            string name = sb.ToString();
            var q = SearchQuery.Parse("e ss ue " + name[..Math.Min(2, name.Length)]);
            foreach (var r in MatchHighlighter.Find(name, q))
            {
                Assert.True(r.Start >= 0 && r.Length > 0 && r.End <= name.Length);
            }
        }
    }

    [Theory]
    [InlineData("Angebot_Mueller_2026-03.pdf", "mueller", "Angebot_|[Mueller]|_2026-03.pdf")]
    [InlineData("Angebot_Mueller_2026-03.pdf", "mueller angebot", "[Angebot]|_|[Mueller]|_2026-03.pdf")]
    [InlineData("Müller Angebote 2026", "mueller angebot", "[Müller]| |[Angebot]|e 2026")]
    [InlineData("Straße 5", "strasse", "[Straße]| 5")]
    [InlineData("notes.txt", "xyz", "notes.txt")]
    [InlineData("notes.txt", "", "notes.txt")]
    [InlineData("notes.txt", "notes.txt", "[notes.txt]")]
    public void Split_GivesPlainAndMarkedPiecesThatAddUpToTheName(string name, string input, string expected)
    {
        var pieces = MatchHighlighter.Split(name, SearchQuery.Parse(input));
        string shown = string.Concat(pieces.Select(p => p.Highlighted ? "[" + p.Text + "]" : p.Text)).Replace("][", string.Empty);
        // The expected text uses | only to separate pieces for readability.
        Assert.Equal(expected.Replace("|", string.Empty), shown);
        Assert.Equal(name, string.Concat(pieces.Select(p => p.Text)));
        for (int i = 1; i < pieces.Count; i++)
        {
            Assert.NotEqual(pieces[i - 1].Highlighted, pieces[i].Highlighted); // plain and marked alternate
        }
    }

    [Fact]
    public void Split_WithoutQuery_OrEmptyName()
    {
        Assert.Equal([new TextSegment("abc", false)], MatchHighlighter.Split("abc", null));
        Assert.Empty(MatchHighlighter.Split("", SearchQuery.Parse("a")));
    }
}
