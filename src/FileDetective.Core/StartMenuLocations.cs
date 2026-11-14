namespace FileDetective.Core;

/// <summary>
/// The Start menu folders as locations (PLAN.md 7.3, "Startmenü"): a Start menu folder that no location contains is added
/// once as a location of its own, so installed programs can be found.
/// </summary>
public static class StartMenuLocations
{
    /// <summary>
    /// The Start menu folders (<see cref="ExcludeRules.AlwaysIncludedPaths"/>, expanded) that lie in none of
    /// <paramref name="roots"/> and exist. Pure text comparison except for <paramref name="directoryExists"/>.
    /// </summary>
    public static IReadOnlyList<string> Missing(
        IReadOnlyList<RootInfo> roots,
        Func<string, bool> directoryExists,
        Func<string, string>? expandEnvironmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(directoryExists);
        var expand = expandEnvironmentVariables ?? Environment.ExpandEnvironmentVariables;
        var missing = new List<string>();
        foreach (string raw in ExcludeRules.AlwaysIncludedPaths)
        {
            if (LocationRules.Normalize(expand(raw)) is not { } path || path.Contains('%', StringComparison.Ordinal))
            {
                continue;
            }

            var candidate = new LocationCandidate(path, null, RootKind.Local);
            bool covered = roots.Any(r => LocationRules.Compare(LocationCandidate.FromRoot(r), candidate)
                is LocationRelation.Same or LocationRelation.FirstContainsSecond);
            if (!covered && !missing.Contains(path, StringComparer.OrdinalIgnoreCase) && directoryExists(path))
            {
                missing.Add(path);
            }
        }

        return missing;
    }
}
