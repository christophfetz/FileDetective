using System.IO.Enumeration;

namespace FileDetective.Core;

/// <summary>
/// Decides which entries the scanner skips (PLAN.md 7.3). Skipped folders are not recorded at all, so their subtree is
/// never listed. Immutable and thread-safe.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>Folder names: a folder whose name equals one of them (case-insensitive) is skipped anywhere.</item>
/// <item>Full paths: environment variables (<c>%WINDIR%</c>) are expanded once when the rules are built; then the path is
/// split into parts at <c>\</c> and <c>/</c>, and each part may contain <c>*</c> (any characters within that one part).
/// An entry is skipped if its full path has the same number of parts and every part matches (case-insensitive).
/// Decision: a rule whose variable cannot be expanded is ignored, and full path rules apply to folders and files.</item>
/// <item>File names: patterns with <c>*</c>, matched against the names of files only.</item>
/// <item>Attribute: entries with <see cref="FileAttributes.System"/> are skipped. Hidden entries without it are kept.</item>
/// </list>
/// The location (root) itself is never checked; the user chose it explicitly.
/// </remarks>
public sealed class ExcludeRules
{
    /// <summary>Default folder names (PLAN.md 7.3).</summary>
    public static readonly IReadOnlyList<string> DefaultFolderNames =
    [
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", "__pycache__", "$RECYCLE.BIN", "System Volume Information",
    ];

    /// <summary>Default full paths (PLAN.md 7.3).</summary>
    public static readonly IReadOnlyList<string> DefaultPaths =
    [
        "%WINDIR%", "%ProgramFiles%", "%ProgramFiles(x86)%", "%ProgramData%", @"C:\Users\*\AppData",
    ];

    /// <summary>Default file name patterns (PLAN.md 7.3).</summary>
    public static readonly IReadOnlyList<string> DefaultFileNames =
    [
        "~$*", "Thumbs.db", "desktop.ini", "*.tmp",
    ];

    private static readonly char[] Separators = ['\\', '/'];

    private readonly HashSet<string> _folderNames;
    private readonly string[] _fileNamePatterns;
    private readonly PathRule[] _pathRules;
    private readonly HashSet<int> _pathRuleDepths;

    /// <param name="folderNames">Folder names skipped anywhere.</param>
    /// <param name="paths">Full paths with environment variables and <c>*</c> per path part.</param>
    /// <param name="fileNamePatterns">File name patterns with <c>*</c>.</param>
    /// <param name="excludeSystem">Skip entries with the System attribute (always true in the program; switchable for tests).</param>
    /// <param name="expandEnvironmentVariables">Expands <c>%NAME%</c>; default <see cref="Environment.ExpandEnvironmentVariables"/>.</param>
    public ExcludeRules(
        IEnumerable<string> folderNames,
        IEnumerable<string> paths,
        IEnumerable<string> fileNamePatterns,
        bool excludeSystem = true,
        Func<string, string>? expandEnvironmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(folderNames);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(fileNamePatterns);
        var expand = expandEnvironmentVariables ?? Environment.ExpandEnvironmentVariables;

        _folderNames = new HashSet<string>(
            folderNames.Select(n => n?.Trim() ?? string.Empty).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
        _fileNamePatterns = [.. fileNamePatterns.Select(p => p?.Trim() ?? string.Empty).Where(p => p.Length > 0)];
        _pathRules = [.. paths.Select(p => PathRule.TryCreate(p, expand)).OfType<PathRule>()];
        _pathRuleDepths = [.. _pathRules.Select(r => r.Parts.Length)];
        ExcludeSystem = excludeSystem;
    }

    /// <summary>Rules with the default lists of PLAN.md 7.3.</summary>
    public static ExcludeRules CreateDefault() => new(DefaultFolderNames, DefaultPaths, DefaultFileNames);

    /// <summary>Rules that skip nothing (for tests and the later storage analysis).</summary>
    public static ExcludeRules None { get; } = new([], [], [], excludeSystem: false);

    public bool ExcludeSystem { get; }

    /// <summary>True if the child <paramref name="entry"/> of the folder <paramref name="parentPath"/> is skipped.</summary>
    public bool IsExcluded(string parentPath, in RawEntry entry)
        => IsExcluded(parentPath, entry.Name, entry.IsDirectory, entry.Attributes);

    /// <summary>True if the child <paramref name="name"/> of the folder <paramref name="parentPath"/> is skipped.</summary>
    public bool IsExcluded(string parentPath, string name, bool isDirectory, FileAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(parentPath);
        ArgumentNullException.ThrowIfNull(name);
        if (ExcludeSystem && (attributes & FileAttributes.System) != 0)
        {
            return true;
        }

        if (isDirectory)
        {
            if (_folderNames.Contains(name))
            {
                return true;
            }
        }
        else
        {
            foreach (string pattern in _fileNamePatterns)
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                {
                    return true;
                }
            }
        }

        return _pathRules.Length > 0 && MatchesPathRule(parentPath, name);
    }

    private bool MatchesPathRule(string parentPath, string name)
    {
        // Cheap pre-check: count the parts of the parent without allocating; most entries have no rule of their depth.
        int depth = CountParts(parentPath) + 1;
        if (!_pathRuleDepths.Contains(depth))
        {
            return false;
        }

        bool unc = parentPath.StartsWith(@"\\", StringComparison.Ordinal) || parentPath.StartsWith("//", StringComparison.Ordinal);
        string[] parentParts = parentPath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rule in _pathRules)
        {
            if (rule.Parts.Length != depth || rule.IsUnc != unc)
            {
                continue;
            }

            bool match = true;
            for (int i = 0; i < depth && match; i++)
            {
                string part = i < parentParts.Length ? parentParts[i] : name;
                match = FileSystemName.MatchesSimpleExpression(rule.Parts[i], part, ignoreCase: true);
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountParts(string path)
    {
        int count = 0;
        bool inPart = false;
        foreach (char c in path)
        {
            if (c is '\\' or '/')
            {
                inPart = false;
            }
            else if (!inPart)
            {
                inPart = true;
                count++;
            }
        }

        return count;
    }

    private sealed record PathRule(string[] Parts, bool IsUnc)
    {
        public static PathRule? TryCreate(string? pattern, Func<string, string> expand)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return null;
            }

            string expanded = expand(pattern.Trim()).Trim();

            // An unexpanded variable (still "%NAME%") would never match a real path; the rule is ignored.
            int first = expanded.IndexOf('%', StringComparison.Ordinal);
            if (first >= 0 && expanded.IndexOf('%', first + 1) > first)
            {
                return null;
            }

            string[] parts = expanded.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            bool unc = expanded.StartsWith(@"\\", StringComparison.Ordinal) || expanded.StartsWith("//", StringComparison.Ordinal);
            return new PathRule(parts, unc);
        }
    }
}
