namespace FileDetective.Core;

/// <summary>Kind of a location (<c>roots.kind</c>).</summary>
public enum RootKind
{
    Local = 0,
    Network = 1,
    Removable = 2,
}

/// <summary>State of the last scan of a location (<c>roots.last_scan_status</c>).</summary>
public enum ScanStatus
{
    Never = 0,
    Running = 1,
    Complete = 2,
    Aborted = 3,
    CompleteWithErrors = 4,
}

/// <summary>A location (row of <c>roots</c>). <see cref="RootEntryId"/> is the entry that carries the whole path.</summary>
public sealed record RootInfo(
    byte Id,
    string Path,
    string? UncPath,
    RootKind Kind,
    bool Enabled,
    long? LastScanStartedUtc,
    long? LastScanCompletedUtc,
    ScanStatus LastScanStatus,
    int LastScanFailedDirs,
    int AttemptsToday,
    int RootEntryId);

/// <summary>A child of a folder as the index currently knows it (input of the scanner's comparison, PLAN.md 7.2).</summary>
public readonly record struct KnownChild(
    int Id, string Name, bool IsDirectory, long Size, long MTimeUnixSeconds, FileAttributes Attributes);

/// <summary>One stored entry (stage 3 addition, used to start a subtree scan at a known folder).</summary>
public sealed record StoredEntry(
    int Id, int ParentId, byte RootId, string Name, bool IsDirectory, long Size, long MTimeUnixSeconds, FileAttributes Attributes);

/// <summary>A new entry.<see cref="Id"/> comes from <see cref="IndexStore.AllocateId"/>.</summary>
public readonly record struct EntryInsert(
    int Id, int ParentId, string Name, bool IsDirectory, long Size, long MTimeUnixSeconds, FileAttributes Attributes);

/// <summary>New size, time and attributes of an existing entry.</summary>
public readonly record struct EntryUpdate(int Id, long Size, long MTimeUnixSeconds, FileAttributes Attributes);

/// <summary>
/// Changes that <see cref="IndexStore.Apply"/> writes in one transaction. Order of application:
/// subtree deletions, insertions (in list order), updates.
/// </summary>
public sealed class ChangeBatch
{
    public List<EntryInsert> Inserts { get; } = [];

    public List<EntryUpdate> Updates { get; } = [];

    /// <summary>Entries that are removed together with all their descendants.</summary>
    public List<int> DeleteSubtrees { get; } = [];

    public int Count => Inserts.Count + Updates.Count + DeleteSubtrees.Count;
}

/// <summary>Result of <see cref="IndexStore.Apply"/>; <see cref="Deleted"/> counts all removed entries including descendants.</summary>
public readonly record struct ApplyResult(int Inserted, int Updated, int Deleted);
