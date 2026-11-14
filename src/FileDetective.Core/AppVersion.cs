using System.Globalization;

namespace FileDetective.Core;

/// <summary>
/// A version in the semver style <c>major.minor.patch[-prerelease][+build]</c> with an optional leading <c>v</c>
/// (release tags are <c>v0.1.0</c>). Missing parts count as 0; the build part is ignored. A release is newer than its
/// own pre-releases.
/// </summary>
public readonly record struct AppVersion : IComparable<AppVersion>
{
    public int Major { get; init; }

    public int Minor { get; init; }

    public int Patch { get; init; }

    /// <summary>Pre-release text without the leading <c>-</c>, empty for a normal release.</summary>
    public string PreRelease { get; init; }

    public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string s = text.Trim();
        if (s[0] is 'v' or 'V')
        {
            s = s[1..];
        }

        int plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            s = s[..plus];
        }

        string pre = string.Empty;
        int dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return false;
            }
        }

        string[] parts = s.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        int[] numbers = new int[3];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new AppVersion { Major = numbers[0], Minor = numbers[1], Patch = numbers[2], PreRelease = pre };
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        if (c != 0)
        {
            return c;
        }

        c = Patch.CompareTo(other.Patch);
        if (c != 0)
        {
            return c;
        }

        return ComparePreRelease(PreRelease ?? string.Empty, other.PreRelease ?? string.Empty);
    }

    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}") + (IsPreRelease ? "-" + PreRelease : string.Empty);

    private static int ComparePreRelease(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return a.Length == b.Length ? 0 : (a.Length == 0 ? 1 : -1); // no pre-release is the newer one
        }

        string[] x = a.Split('.');
        string[] y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xn = x[i].All(char.IsAsciiDigit);
            bool yn = y[i].All(char.IsAsciiDigit);
            int c;
            if (xn && yn)
            {
                string xs = x[i].TrimStart('0');
                string ys = y[i].TrimStart('0');
                c = xs.Length.CompareTo(ys.Length); // the longer number is the bigger one
                if (c == 0)
                {
                    c = string.CompareOrdinal(xs, ys);
                }
            }
            else if (xn != yn)
            {
                c = xn ? -1 : 1; // numeric identifiers are lower than text ones
            }
            else
            {
                c = string.CompareOrdinal(x[i], y[i]);
            }

            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
