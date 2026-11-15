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

/// <summary>
/// A row of <c>hashes</c> (stage 10, PLAN.md 11.2): SHA-256 of the first 64 KB (<see cref="Partial"/>) and of the whole
/// file (<see cref="Full"/>, null until needed). Valid only while the file still has <see cref="Size"/> and
/// <see cref="MTimeTicks"/> (last write time, UTC ticks).
/// </summary>
public sealed record StoredHash(int EntryId, long Size, long MTimeTicks, byte[]? Partial, byte[]? Full)
{
    public bool IsValidFor(long size, long mtimeTicks) => Size == size && MTimeTicks == mtimeTicks;
}

/// <summary>
/// A row of <c>ocr</c> (stage 11, PLAN.md 11.3): the text Windows recognized in an image. Valid only while the image still
/// has <see cref="Size"/> and <see cref="MTimeTicks"/> (last write time, UTC ticks).
/// </summary>
public sealed record StoredImageText(int EntryId, long Size, long MTimeTicks, string Text)
{
    public bool IsValidFor(long size, long mtimeTicks) => Size == size && MTimeTicks == mtimeTicks;

    /// <summary>
    /// True if the row still fits the size and the time of the entry in the directory (whole Unix seconds there): checked
    /// at start without touching the file.
    /// </summary>
    public bool MatchesEntry(long entrySize, long entryMTimeUnixSeconds)
        => Size == entrySize
            && IndexStore.ToUnixSeconds(new DateTime(Math.Clamp(MTimeTicks, 0, DateTime.MaxValue.Ticks), DateTimeKind.Utc)) == entryMTimeUnixSeconds;
}

/// <summary>A row of <c>notes</c> (stage 13, PLAN.md 11.4): the user's own words for an image, keyed by its full path.</summary>
public sealed record StoredImageNote(string Path, string Text, long ChangedUnixSeconds);

/// <summary>A file written by a change: inserted (with its name) or updated (name null); also a moved or renamed entry.</summary>
public readonly record struct WrittenFile(int Id, string? Name);

/// <summary>
/// What one change of the directory did (stage 11, <see cref="IndexStore.Changed"/>): files written, entries removed
/// (with all descendants), whether an entry was moved or renamed, whether the whole directory was emptied.
/// </summary>
public sealed record IndexChange(IReadOnlyList<WrittenFile> Written, IReadOnlyList<int> Deleted, bool Relocated, bool Reset);

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
