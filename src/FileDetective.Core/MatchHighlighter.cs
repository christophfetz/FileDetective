using System.Globalization;
using System.Text;

namespace FileDetective.Core;

/// <summary>A range of characters in a string (UTF-16 code units).</summary>
public readonly record struct MatchRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>
/// Finds where the search terms of a query occur in the original name, for highlighting in the hit list (PLAN.md 8.4).
/// </summary>
/// <remarks>
/// The index compares search forms (<see cref="SearchNormalizer"/>), which can differ in length from the original
/// (<c>ä</c> becomes <c>ae</c>, <c>ß</c> becomes <c>ss</c>, a decomposed <c>u</c> + U+0308 becomes <c>ue</c>, accents
/// disappear). The name is therefore cut into text elements (a base character with its combining marks, a surrogate
/// pair), each element is normalized on its own, and every byte of the search form remembers its element. A hit of a
/// term in the search form then marks all elements it touches: <c>mueller</c> in <c>Müller</c> marks the whole word, and
/// <c>ue</c> marks only the <c>ü</c>. A hit that starts or ends inside an element (<c>u</c> in <c>ü</c> = <c>ue</c>)
/// marks the whole element, because half a character cannot be marked. If the per-element forms do not add up to
/// the search form of the whole name (very exotic scripts, lone surrogates), the whole name counts as one element:
/// a coarse but never wrong marking. Every occurrence of every term is marked; overlapping and touching ranges are merged.
/// </remarks>
public static class MatchHighlighter
{
    /// <summary>The ranges of <paramref name="name"/> that the terms of <paramref name="query"/> match, sorted and without overlap.</summary>
    public static IReadOnlyList<MatchRange> Find(string name, SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Find(name, query.TermBytes);
    }

    /// <summary>Same with terms given in search form (UTF-8 bytes).</summary>
    public static IReadOnlyList<MatchRange> Find(string name, IReadOnlyList<byte[]> terms)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(terms);
        if (name.Length == 0 || terms.Count == 0)
        {
            return [];
        }

        var ranges = new List<MatchRange>();
        if (Ascii.IsValid(name))
        {
            // Search form = lower case, byte i is character i.
            var lower = new byte[name.Length];
            Ascii.ToLower(name, lower, out _);
            foreach (var term in terms)
            {
                foreach (var (from, to) in Occurrences(lower, term))
                {
                    ranges.Add(new MatchRange(from, to - from));
                }
            }

            return Merge(ranges);
        }

        var map = BuildMap(name);
        foreach (var term in terms)
        {
            foreach (var (from, to) in Occurrences(map.Form, term))
            {
                int start = map.ElementStart[map.ElementOfByte[from]];
                int end = map.ElementEnd[map.ElementOfByte[to - 1]];
                ranges.Add(new MatchRange(start, end - start));
            }
        }

        return Merge(ranges);
    }

    /// <summary>
    /// Cuts <paramref name="name"/> at the ranges of <see cref="Find(string, SearchQuery)"/>: alternating plain and marked
    /// pieces, in order, that together give the name again. One plain piece if there is no query or no hit; no piece at all
    /// for an empty name.
    /// </summary>
    public static IReadOnlyList<TextSegment> Split(string name, SearchQuery? query)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0)
        {
            return [];
        }

        var ranges = query is null ? [] : Find(name, query);
        if (ranges.Count == 0)
        {
            return [new TextSegment(name, false)];
        }

        var segments = new List<TextSegment>(ranges.Count * 2 + 1);
        int position = 0;
        foreach (var range in ranges)
        {
            if (range.Start > position)
            {
                segments.Add(new TextSegment(name[position..range.Start], false));
            }

            segments.Add(new TextSegment(name.Substring(range.Start, range.Length), true));
            position = range.End;
        }

        if (position < name.Length)
        {
            segments.Add(new TextSegment(name[position..], false));
        }

        return segments;
    }

    private static IEnumerable<(int From, int To)> Occurrences(byte[] form, byte[] term)
    {
        if (term.Length == 0)
        {
            yield break;
        }

        int offset = 0;
        while (offset <= form.Length - term.Length)
        {
            int index = form.AsSpan(offset).IndexOf(term);
            if (index < 0)
            {
                yield break;
            }

            int at = offset + index;
            yield return (at, at + term.Length);
            offset = at + 1; // overlapping hits are merged later
        }
    }

    private static List<MatchRange> Merge(List<MatchRange> ranges)
    {
        if (ranges.Count <= 1)
        {
            return ranges;
        }

        ranges.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<MatchRange>(ranges.Count) { ranges[0] };
        for (int i = 1; i < ranges.Count; i++)
        {
            var last = merged[^1];
            var next = ranges[i];
            if (next.Start <= last.End)
            {
                merged[^1] = new MatchRange(last.Start, Math.Max(last.End, next.End) - last.Start);
            }
            else
            {
                merged.Add(next);
            }
        }

        return merged;
    }

    /// <summary>The search form of a name and, for each of its bytes, the text element of the original it came from.</summary>
    private sealed class FormMap(byte[] form, int[] elementOfByte, int[] elementStart, int[] elementEnd)
    {
        public byte[] Form { get; } = form;

        public int[] ElementOfByte { get; } = elementOfByte;

        public int[] ElementStart { get; } = elementStart;

        public int[] ElementEnd { get; } = elementEnd;
    }

    private static FormMap BuildMap(string name)
    {
        byte[] whole = SearchNormalizer.Normalize(name);

        var starts = new List<int>();
        var ends = new List<int>();
        var forms = new List<byte[]>();
        int total = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(name);
        while (enumerator.MoveNext())
        {
            int start = enumerator.ElementIndex;
            string element = (string)enumerator.Current;
            byte[] form = SearchNormalizer.Normalize(element);
            starts.Add(start);
            ends.Add(start + element.Length);
            forms.Add(form);
            total += form.Length;
        }

        bool consistent = total == whole.Length;
        if (consistent)
        {
            int position = 0;
            foreach (var form in forms)
            {
                if (!whole.AsSpan(position, form.Length).SequenceEqual(form))
                {
                    consistent = false;
                    break;
                }

                position += form.Length;
            }
        }

        if (!consistent)
        {
            // Fallback: the whole name is one element.
            return new FormMap(whole, new int[whole.Length], [0], [name.Length]);
        }

        var elementOfByte = new int[whole.Length];
        int offset = 0;
        for (int e = 0; e < forms.Count; e++)
        {
            for (int b = 0; b < forms[e].Length; b++)
            {
                elementOfByte[offset++] = e;
            }
        }

        return new FormMap(whole, elementOfByte, [.. starts], [.. ends]);
    }
}

/// <summary>A piece of a name: plain or marked as a place where a search term was found.</summary>
public readonly record struct TextSegment(string Text, bool Highlighted);
