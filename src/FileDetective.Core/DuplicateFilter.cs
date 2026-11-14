namespace FileDetective.Core;

/// <summary>
/// Filter of the duplicates window (PLAN.md 11.2, "Programm- und Datenbankdateien"): groups made only of files that belong
/// to programs (libraries, executables, databases, build output) are hidden by default. Such copies are needed where they
/// lie, and removing one breaks a program. The filter only hides; it never changes what the search finds.
/// </summary>
public static class DuplicateFilter
{
    /// <summary>File endings that count as program or database files.</summary>
    public static readonly IReadOnlyList<string> TechnicalExtensions =
    [
        ".dll", ".exe", ".pdb", ".sys", ".ocx", ".msi", ".so", ".pyd", ".jar", ".node",
        ".db", ".sqlite", ".sqlite3", ".db-wal", ".db-shm", ".mdf", ".ldf",
    ];

    /// <summary>Folder names of build output and package caches; every file below one counts as technical.</summary>
    public static readonly IReadOnlyList<string> TechnicalFolderNames = ["bin", "obj", "publish", "packages", ".nuget"];

    private static readonly HashSet<string> Extensions = new(TechnicalExtensions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Folders = new(TechnicalFolderNames, StringComparer.OrdinalIgnoreCase);
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>True if the file has a technical ending or lies below a build or package folder.</summary>
    public static bool IsTechnical(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        if (Extensions.Contains(Path.GetExtension(fullPath)))
        {
            return true;
        }

        string[] parts = fullPath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (Folders.Contains(parts[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True if every file of the group is technical. A group with one ordinary file (a photo that also lies in a
    /// <c>bin</c> folder) stays visible.
    /// </summary>
    public static bool IsTechnical(DuplicateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return group.Files.Count > 0 && group.Files.All(f => IsTechnical(f.FullPath));
    }

    /// <summary>
    /// Splits the groups into the visible ones and the number of hidden ones. With <paramref name="hideTechnical"/>,
    /// the marks of hidden groups are cleared, so "Move to Recycle Bin" can never act on a file that is not shown.
    /// </summary>
    public static (IReadOnlyList<DuplicateGroup> Visible, int Hidden) Apply(IReadOnlyList<DuplicateGroup> groups, bool hideTechnical)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (!hideTechnical)
        {
            return (groups, 0);
        }

        var visible = new List<DuplicateGroup>(groups.Count);
        var hidden = new List<DuplicateGroup>();
        foreach (var group in groups)
        {
            (IsTechnical(group) ? hidden : visible).Add(group);
        }

        DuplicateMarking.ClearMarks(hidden);
        return (visible, hidden.Count);
    }
}
