using System.Text;
using FileDetective.Core;

namespace FileDetective.Tests;

public class SearchNormalizerTests
{
    private static string N(string s) => Encoding.UTF8.GetString(SearchNormalizer.Normalize(s));

    [Fact]
    public void Mueller_VariantsGiveSameBytes()
    {
        var expected = SearchNormalizer.Normalize("mueller");
        Assert.Equal(expected, SearchNormalizer.Normalize("Müller"));
        Assert.Equal(expected, SearchNormalizer.Normalize("MUELLER"));
        Assert.Equal(expected, SearchNormalizer.Normalize("Müller")); // decomposed, as stored by macOS
        Assert.Equal(expected, SearchNormalizer.Normalize("MÜLLER"));
    }

    [Theory]
    [InlineData("Café", "cafe")]
    [InlineData("élève", "eleve")]
    [InlineData("Crème Brûlée", "creme brulee")]
    [InlineData("Ångström", "angstroem")]
    public void Accents_AreRemoved(string input, string expected) => Assert.Equal(expected, N(input));

    [Theory]
    [InlineData("Straße", "strasse")]
    [InlineData("STRAẞE", "strasse")]
    [InlineData("Größe Äpfel Öl", "groesse aepfel oel")]
    public void GermanLetters_AreSpelledOut(string input, string expected) => Assert.Equal(expected, N(input));

    [Fact]
    public void Ascii_FastPathOnlyLowersLetters()
    {
        Assert.Equal("report_2024-final (1).pdf", N("Report_2024-FINAL (1).PDF"));
    }

    [Fact]
    public void Output_IsUtf8()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("日本語"), SearchNormalizer.Normalize("日本語"));
    }

    [Fact]
    public void LoneSurrogate_DoesNotThrow()
    {
        var bytes = SearchNormalizer.Normalize("A\uD800b");
        Assert.Equal(SearchNormalizer.Normalize("a\uD800B"), bytes);
    }

    [Fact]
    public void KnownLimit_MullerDoesNotEqualMueller()
    {
        Assert.NotEqual(SearchNormalizer.Normalize("muller"), SearchNormalizer.Normalize("Müller"));
    }
}
