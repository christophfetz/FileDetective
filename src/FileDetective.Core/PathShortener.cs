namespace FileDetective.Core;

/// <summary>Shortens long folder paths for display (PLAN.md 8.4: "in der Mitte gekürzt").</summary>
public static class PathShortener
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Returns <paramref name="path"/> unchanged if it has at most <paramref name="maxLength"/> characters. Otherwise
    /// the start of the path (drive or <c>\\server\share\</c>) and as many whole trailing folders as fit are kept and
    /// the middle is replaced by an ellipsis, e.g. <c>C:\Users\…\Offers\</c>. If not even the last folder fits,
    /// the tail is cut by characters; if the start alone is too long, the text is cut in the middle by characters.
    /// The result never exceeds <paramref name="maxLength"/> characters (for a maximum of at least 3).
    /// </summary>
    public static string ShortenMiddle(string path, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 3);
        if (path.Length <= maxLength)
        {
            return path;
        }

        int headLength = HeadLength(path);
        int tailBudget = maxLength - headLength - Ellipsis.Length;
        if (headLength == 0 || tailBudget < 4)
        {
            return CutMiddle(path, maxLength);
        }

        // Whole trailing segments: the tail starts at a separator inside the last tailBudget characters.
        int start = path.Length - tailBudget;
        int separator = path.IndexOf('\\', start);
        if (separator >= 0 && separator < path.Length - 1)
        {
            return path[..headLength] + Ellipsis + path[separator..];
        }

        // No whole segment fits (or only the trailing separator): cut the last characters.
        return path[..headLength] + Ellipsis + path[^tailBudget..];
    }

    // Length of "C:\" or "\\server\share\" (including the separator); 0 if the path has no recognizable start.
    private static int HeadLength(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            int server = path.IndexOf('\\', 2);
            if (server < 0)
            {
                return 0;
            }

            int share = path.IndexOf('\\', server + 1);
            return share < 0 ? 0 : share + 1;
        }

        if (path.Length >= 3 && path[1] == ':' && path[2] == '\\')
        {
            return 3;
        }

        return 0;
    }

    private static string CutMiddle(string text, int maxLength)
    {
        int keep = maxLength - Ellipsis.Length;
        int front = (keep + 1) / 2;
        int back = keep - front;
        return text[..front] + Ellipsis + text[^back..];
    }
}
