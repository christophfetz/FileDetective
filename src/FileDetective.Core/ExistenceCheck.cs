namespace FileDetective.Core;

/// <summary>Result of <see cref="ExistenceCheck.Evaluate"/>.</summary>
public enum ExistenceResult
{
    /// <summary>The file or folder exists.</summary>
    Exists,

    /// <summary>It is gone, and the parent folder and the location were readable: safe to remove it from the directory.</summary>
    Vanished,

    /// <summary>Not found, but the reason is unclear (location or parent not reachable): the directory must stay unchanged.</summary>
    Unknown,
}

/// <summary>Asks whether a file or folder exists (decouples the check from the file system for tests).</summary>
public interface IPathProbe
{
    bool Exists(string path);
}

/// <summary>Real file system: true for a file or a folder.</summary>
public sealed class FileSystemPathProbe : IPathProbe
{
    public static FileSystemPathProbe Instance { get; } = new();

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}

/// <summary>
/// Decides what it means that a hit cannot be found when it is opened (PLAN.md 8.4 and 2.5): the entry is only
/// reported as <see cref="ExistenceResult.Vanished"/> if the location and the parent folder can be seen but the entry
/// cannot. <c>File.Exists</c> also returns false on a dead share or without access, and that must never remove entries.
/// </summary>
public static class ExistenceCheck
{
    public static ExistenceResult Evaluate(string path, IReadOnlyList<string> rootPaths, IPathProbe probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rootPaths);
        ArgumentNullException.ThrowIfNull(probe);

        if (probe.Exists(path))
        {
            return ExistenceResult.Exists;
        }

        string? root = null;
        foreach (var candidate in rootPaths)
        {
            string trimmed = candidate.TrimEnd('\\', '/');
            bool prefix = path.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase)
                && (path.Length == trimmed.Length || path[trimmed.Length] is '\\' or '/');
            if (prefix && (root is null || candidate.Length > root.Length))
            {
                root = candidate;
            }
        }

        string? parent = Path.GetDirectoryName(path);
        if (root is null || string.IsNullOrEmpty(parent) || !probe.Exists(root) || !probe.Exists(parent))
        {
            return ExistenceResult.Unknown;
        }

        // The location itself is never "vanished" here: removing a whole location is a settings action (stage 5).
        return string.Equals(path.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? ExistenceResult.Unknown
            : ExistenceResult.Vanished;
    }
}
