using System.Globalization;
using System.Text;

namespace FileDetective.Core;

/// <summary>
/// Turns a name or a search term into its search form (PLAN.md 6.2): UTF-8 bytes,
/// lower case, German umlauts spelled out, other accents removed.
/// </summary>
public static class SearchNormalizer
{
    /// <summary>Returns the search form of <paramref name="text"/> as UTF-8 bytes.</summary>
    public static byte[] Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Fast path for pure ASCII names: only A-Z needs lowering, nothing else changes.
        if (Ascii.IsValid(text))
        {
            var bytes = new byte[text.Length];
            Ascii.ToLower(text, bytes, out _);
            return bytes;
        }

        // 1. Composed form, so that umlauts stored decomposed (e.g. by macOS) are single characters.
        string s = SafeNormalize(text, NormalizationForm.FormC);

        // 2. Culture-independent lower case.
        s = s.ToLowerInvariant();

        // 3. German umlauts and sharp s.
        var sb = new StringBuilder(s.Length + 8);
        foreach (char c in s)
        {
            switch (c)
            {
                case 'ä': sb.Append("ae"); break;
                case 'ö': sb.Append("oe"); break;
                case 'ü': sb.Append("ue"); break;
                case 'ß': sb.Append("ss"); break;
                default: sb.Append(c); break;
            }
        }

        // 4. Remaining accents: decompose and drop non-spacing marks.
        string decomposed = SafeNormalize(sb.ToString(), NormalizationForm.FormD);
        sb.Clear();
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        // Decision (not in the plan): recompose at the end so that characters that only
        // decompose without marks (e.g. Hangul) are stored compactly and identically for names and terms.
        return Encoding.UTF8.GetBytes(SafeNormalize(sb.ToString(), NormalizationForm.FormC));
    }

    /// <summary>
    /// Windows file names may contain unpaired surrogates, for which <see cref="string.Normalize(NormalizationForm)"/>
    /// throws. Decision: such strings are left unnormalized instead of failing (UTF-8 encoding then
    /// replaces the lone surrogate with U+FFFD, consistently for names and terms).
    /// </summary>
    private static string SafeNormalize(string s, NormalizationForm form)
    {
        try
        {
            return s.Normalize(form);
        }
        catch (ArgumentException)
        {
            return s;
        }
    }
}
