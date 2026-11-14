namespace FileDetective.Core;

/// <summary>
/// A location as chosen by the user. <see cref="Path"/> is as chosen (<c>P:\Projekte</c> or <c>\\server\share\x</c>);
/// <see cref="UncPath"/> is the UNC form of a path on a mapped network drive and null otherwise. Both count as names of
/// the same place in <see cref="LocationRules"/>.
/// </summary>
public sealed record LocationCandidate(string Path, string? UncPath, RootKind Kind)
{
    public static LocationCandidate FromRoot(RootInfo root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new LocationCandidate(root.Path, root.UncPath, root.Kind);
    }
}

/// <summary>How two locations relate to each other (<see cref="LocationRules.Compare"/>).</summary>
public enum LocationRelation
{
    Unrelated,

    /// <summary>Both name the same folder.</summary>
    Same,

    /// <summary>The first location contains the second one.</summary>
    FirstContainsSecond,

    /// <summary>The first location lies inside the second one.</summary>
    FirstInsideSecond,
}

/// <summary>Result of <see cref="LocationRules.Check"/>.</summary>
public enum LocationCheckResult
{
    /// <summary>Can be added as it is.</summary>
    Free,

    /// <summary>The same place is a location already (<see cref="LocationCheck.Related"/> has it).</summary>
    AlreadyExists,

    /// <summary>The candidate lies inside an existing location (<see cref="LocationCheck.Related"/> has it): not allowed.</summary>
    InsideExisting,

    /// <summary>The candidate contains existing locations (<see cref="LocationCheck.Related"/>): adding it replaces them.</summary>
    ContainsExisting,
}

/// <summary>What <see cref="LocationRules.Check"/> found out and which existing locations are involved.</summary>
public sealed record LocationCheck(LocationCheckResult Result, IReadOnlyList<RootInfo> Related);

/// <summary>
/// Rules for locations (PLAN.md 8.5): nested locations are not allowed, the outer one stays. All comparisons are text
/// comparisons without any file system or network access; a path is compared in parts, so <c>C:\Daten</c> does not
/// contain <c>C:\Daten2</c>, and case does not matter. A drive letter and the UNC path of the same target count as the
/// same place if the letter is a mapped network drive (that is what <see cref="LocationCandidate.UncPath"/> records).
/// </summary>
public static class LocationRules
{
    private static readonly char[] InvalidNameChars = ['<', '>', '"', '|', '?', '*'];

    /// <summary>
    /// Brings a path into one form: backslashes, no trailing backslash except on a drive root (<c>C:\</c>), no repeated
    /// separators, <c>.</c> and <c>..</c> resolved. Returns null if the text is not an absolute drive or UNC path
    /// (a UNC path needs server and share; <c>\\?\</c> paths and relative paths are refused).
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string text = path.Trim().Replace('/', '\\');
        bool unc = text.StartsWith(@"\\", StringComparison.Ordinal);
        var parts = new List<string>();
        foreach (string part in text.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(part);
        }

        if (unc)
        {
            // \\server\share[\...]: the first two parts are the server and the share and cannot be removed by "..".
            if (parts.Count < 2 || parts.Any(p => p.IndexOfAny(InvalidNameChars) >= 0))
            {
                return null;
            }

            return @"\\" + string.Join('\\', parts);
        }

        if (parts.Count == 0 || parts[0].Length != 2 || !char.IsAsciiLetter(parts[0][0]) || parts[0][1] != ':')
        {
            return null;
        }

        if (parts.Skip(1).Any(p => p.IndexOfAny(InvalidNameChars) >= 0 || p.Contains(':', StringComparison.Ordinal)))
        {
            return null;
        }

        string drive = char.ToUpperInvariant(parts[0][0]) + ":";
        return parts.Count == 1 ? drive + @"\" : drive + @"\" + string.Join('\\', parts.Skip(1));
    }

    /// <summary>
    /// True if <paramref name="outer"/> is the same as or contains <paramref name="inner"/>. Both must be normalized.
    /// The comparison is at a path boundary and ignores case.
    /// </summary>
    public static bool ContainsOrEquals(string outer, string inner)
    {
        if (!inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return inner.Length == outer.Length || outer.EndsWith('\\') || inner[outer.Length] == '\\';
    }

    /// <summary>Relation of two locations; every name of one is compared with every name of the other.</summary>
    public static LocationRelation Compare(LocationCandidate first, LocationCandidate second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var a = Names(first);
        var b = Names(second);

        bool same = false;
        bool firstContains = false;
        bool firstInside = false;
        foreach (string x in a)
        {
            foreach (string y in b)
            {
                if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
                {
                    same = true;
                }
                else if (ContainsOrEquals(x, y))
                {
                    firstContains = true;
                }
                else if (ContainsOrEquals(y, x))
                {
                    firstInside = true;
                }
            }
        }

        return same ? LocationRelation.Same
            : firstContains ? LocationRelation.FirstContainsSecond
            : firstInside ? LocationRelation.FirstInsideSecond
            : LocationRelation.Unrelated;
    }

    /// <summary>
    /// Checks a new location against the existing ones. Inside an existing location (or the same place) wins over
    /// containing others, so the result is always one clear case.
    /// </summary>
    public static LocationCheck Check(LocationCandidate candidate, IReadOnlyList<RootInfo> existing)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(existing);
        var same = new List<RootInfo>();
        var outer = new List<RootInfo>();
        var inner = new List<RootInfo>();
        foreach (var root in existing)
        {
            switch (Compare(candidate, LocationCandidate.FromRoot(root)))
            {
                case LocationRelation.Same:
                    same.Add(root);
                    break;
                case LocationRelation.FirstInsideSecond:
                    outer.Add(root);
                    break;
                case LocationRelation.FirstContainsSecond:
                    inner.Add(root);
                    break;
            }
        }

        if (same.Count > 0)
        {
            return new LocationCheck(LocationCheckResult.AlreadyExists, same);
        }

        if (outer.Count > 0)
        {
            return new LocationCheck(LocationCheckResult.InsideExisting, outer);
        }

        return inner.Count > 0
            ? new LocationCheck(LocationCheckResult.ContainsExisting, inner)
            : new LocationCheck(LocationCheckResult.Free, []);
    }

    /// <summary>
    /// Keeps only the outermost locations, in the given order. Of several that name the same place the first one stays.
    /// A candidate whose path cannot be normalized is dropped.
    /// </summary>
    public static IReadOnlyList<LocationCandidate> KeepOutermost(IReadOnlyList<LocationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var kept = new List<LocationCandidate>();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (Names(candidates[i]).Length > 0 && FindOuter(candidates, i) is null)
            {
                kept.Add(candidates[i]);
            }
        }

        return kept;
    }

    /// <summary>
    /// The location in the list that makes <c>candidates[index]</c> redundant (contains it, or is the same place and comes
    /// earlier), or null if the candidate stays.
    /// </summary>
    public static LocationCandidate? FindOuter(IReadOnlyList<LocationCandidate> candidates, int index)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        for (int j = 0; j < candidates.Count; j++)
        {
            if (j == index)
            {
                continue;
            }

            var relation = Compare(candidates[j], candidates[index]);
            if (relation == LocationRelation.FirstContainsSecond || (relation == LocationRelation.Same && j < index))
            {
                return candidates[j];
            }
        }

        return null;
    }

    private static string[] Names(LocationCandidate candidate)
    {
        var names = new List<string>(2);
        if (Normalize(candidate.Path) is { } path)
        {
            names.Add(path);
        }

        if (Normalize(candidate.UncPath) is { } unc)
        {
            names.Add(unc);
        }

        return [.. names];
    }
}
