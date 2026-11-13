using System.Text;

namespace FileDetective.Core;

/// <summary>Which kinds of entries a search returns (Tab in the search bar).</summary>
public enum SearchMode
{
    All,
    Files,
    Folders,
}

/// <summary>
/// A parsed search input (PLAN.md 6.4, "Eingabe zerlegen"). All terms and type filters are in search form.
/// </summary>
public sealed class SearchQuery
{
    /// <summary>At most this many search terms are used; further terms are ignored.</summary>
    public const int MaxTerms = 8;

    /// <summary>
    /// At most this many type filters are used. Decision: the plan only limits terms; type filters
    /// do not need a bit in the mask, but are capped the same way to keep the work per entry bounded.
    /// </summary>
    public const int MaxTypeFilters = 8;

    /// <summary>Maximum number of letters/digits after the dot of a type filter.</summary>
    public const int MaxTypeFilterLength = 10;

    private SearchQuery(byte[][] terms, byte[][] typeFilters, SearchMode mode)
    {
        TermBytes = terms;
        TypeFilterBytes = typeFilters;
        Mode = mode;
        Terms = Array.ConvertAll(terms, t => Encoding.UTF8.GetString(t));
        TypeFilters = Array.ConvertAll(typeFilters, t => Encoding.UTF8.GetString(t));

        // Decision: "Name ist genau die Eingabe" compares against the terms joined by single spaces.
        ExactBytes = Encoding.UTF8.GetBytes(string.Join(' ', Terms));
    }

    /// <summary>The search terms in search form, in input order (at most <see cref="MaxTerms"/>).</summary>
    public IReadOnlyList<string> Terms { get; }

    /// <summary>The type filters in search form including the leading dot, e.g. <c>.pdf</c>.</summary>
    public IReadOnlyList<string> TypeFilters { get; }

    public SearchMode Mode { get; }

    /// <summary>True if there is neither a term nor a type filter; such a query has no hits.</summary>
    public bool IsEmpty => TermBytes.Length == 0 && TypeFilterBytes.Length == 0;

    internal byte[][] TermBytes { get; }

    internal byte[][] TypeFilterBytes { get; }

    internal byte[] ExactBytes { get; }

    /// <summary>
    /// Splits the input at white space; text in double quotes stays one term. Terms containing
    /// <c>\</c> or <c>/</c> are split there. <c>.</c> plus 1–10 letters/digits is a type filter.
    /// </summary>
    public static SearchQuery Parse(string? input, SearchMode mode = SearchMode.All)
    {
        var terms = new List<byte[]>();
        var filters = new List<byte[]>();

        foreach (var (token, quoted) in Tokenize(input ?? string.Empty))
        {
            // Decision: a quoted token is always a literal term, never a type filter.
            if (!quoted && IsTypeFilter(token))
            {
                if (filters.Count < MaxTypeFilters)
                {
                    filters.Add(SearchNormalizer.Normalize(token));
                }

                continue;
            }

            // Names never contain \ or /, so such a term is split, also inside quotes.
            foreach (var part in token.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (terms.Count >= MaxTerms)
                {
                    break;
                }

                var normalized = SearchNormalizer.Normalize(part);
                if (normalized.Length > 0)
                {
                    terms.Add(normalized);
                }
            }
        }

        return new SearchQuery([.. terms], [.. filters], mode);
    }

    private static bool IsTypeFilter(string token)
    {
        if (token.Length < 2 || token.Length > MaxTypeFilterLength + 1 || token[0] != '.')
        {
            return false;
        }

        for (int i = 1; i < token.Length; i++)
        {
            if (!char.IsLetterOrDigit(token[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<(string Token, bool Quoted)> Tokenize(string input)
    {
        var current = new StringBuilder();
        bool inQuotes = false;
        bool quoted = false;

        foreach (char c in input)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                quoted = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    yield return (current.ToString(), quoted);
                }

                current.Clear();
                quoted = false;
            }
            else
            {
                current.Append(c);
            }
        }

        // An unterminated quote simply runs to the end of the input.
        if (current.Length > 0)
        {
            yield return (current.ToString(), quoted);
        }
    }
}
