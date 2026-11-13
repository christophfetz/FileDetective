using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace FileDetective.Core;

/// <summary>One entry of a directory listing (PLAN.md 7.1).</summary>
public readonly record struct RawEntry(
    string Name, bool IsDirectory, long Size, DateTime LastWriteUtc, FileAttributes Attributes);

/// <summary>Lists one directory (PLAN.md 7.1). Implemented for real by <see cref="FileSystemDirectoryLister"/>; tests use fakes.</summary>
public interface IDirectoryLister
{
    /// <summary>
    /// Returns the complete content of a directory or throws an exception. Never returns a partial result.
    /// </summary>
    IReadOnlyList<RawEntry> List(string directoryPath);
}

/// <summary>
/// Real <see cref="IDirectoryLister"/> on top of <see cref="FileSystemEnumerable{TResult}"/>: one request per directory
/// returns name, size, time and attributes of all children; no file is ever opened (PLAN.md 2.1, 7.1).
/// </summary>
public sealed class FileSystemDirectoryLister : IDirectoryLister
{
    public static FileSystemDirectoryLister Instance { get; } = new();

    public IReadOnlyList<RawEntry> List(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
            // Never true (PLAN.md 7.1): an access error would come back as an empty list and the comparison would
            // delete the whole subtree.
            IgnoreInaccessible = false,
        };

        var enumerable = new FileSystemEnumerable<RawEntry>(
            directoryPath,
            static (ref FileSystemEntry e) => new RawEntry(
                e.FileName.ToString(),
                e.IsDirectory,
                e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.UtcDateTime,
                e.Attributes),
            options);

        // Any exception while enumerating propagates; the partial list is dropped with this local variable.
        var result = new List<RawEntry>();
        foreach (var entry in enumerable)
        {
            result.Add(entry);
        }

        return result;
    }
}

/// <summary>
/// Decides whether a directory with the <see cref="FileAttributes.ReparsePoint"/> attribute is a link (symbolic link,
/// junction) that the scanner records but never enters (PLAN.md 7.2 point 4).
/// </summary>
public interface ILinkDetector
{
    /// <summary>True if the directory at <paramref name="directoryPath"/> is a link and must not be entered.</summary>
    bool IsLink(string directoryPath);
}

/// <summary>
/// Real <see cref="ILinkDetector"/>. Reads the reparse tag from the directory listing of the parent
/// (<c>FindFirstFileExW</c>, <c>WIN32_FIND_DATAW.dwReserved0</c>) instead of using <see cref="FileSystemInfo.LinkTarget"/>.
/// </summary>
/// <remarks>
/// Decision: <see cref="FileSystemInfo.LinkTarget"/> opens the reparse point itself (<c>CreateFile</c> plus
/// <c>FSCTL_GET_REPARSE_POINT</c>). On a network share that would be an open of something other than a directory
/// listing (PLAN.md 2.1). The tag comes with the listing instead, so the same rule is used for every kind of location.
/// A directory counts as a link when its tag is a "name surrogate" (bit 29), which covers exactly the tags for which
/// <see cref="FileSystemInfo.LinkTarget"/> is not null (<c>IO_REPARSE_TAG_SYMLINK</c>, <c>IO_REPARSE_TAG_MOUNT_POINT</c>)
/// plus other alias tags; OneDrive/cloud and DFS tags are no name surrogates and are entered normally. If the tag cannot
/// be read (error, or tag 0 although the attribute is set) the directory is conservatively treated as a link: better a
/// folder too few in the index than a loop or an unintended access.
/// </remarks>
public sealed class ReparseTagLinkDetector : ILinkDetector
{
    private const uint NameSurrogateBit = 0x2000_0000;
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int Win32FindDataSize = 592;
    private const int AttributesOffset = 0;
    private const int Reserved0Offset = 36;

    public static ReparseTagLinkDetector Instance { get; } = new();

    public bool IsLink(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);
        uint? tag = TryGetReparseTag(directoryPath);
        return tag is not { } t || t == 0 || (t & NameSurrogateBit) != 0;
    }

    /// <summary>
    /// The reparse tag of the entry as reported by the listing of its parent; 0 if the entry is no reparse point;
    /// null if it could not be read.
    /// </summary>
    public static uint? TryGetReparseTag(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string p = path.TrimEnd('\\', '/');
        if (p.Length >= 248 && !p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
        }

        nint buffer = Marshal.AllocHGlobal(Win32FindDataSize);
        try
        {
            nint handle = FindFirstFileExW(p, FindExInfoBasic, buffer, FindExSearchNameMatch, 0, 0);
            if (handle == -1)
            {
                return null;
            }

            FindClose(handle);
            var attributes = (FileAttributes)Marshal.ReadInt32(buffer, AttributesOffset);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return 0;
            }

            return unchecked((uint)Marshal.ReadInt32(buffer, Reserved0Offset));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

#pragma warning disable SYSLIB1054 // LibraryImport would need AllowUnsafeBlocks; this one call is fine with DllImport.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint FindFirstFileExW(
        string fileName, int infoLevel, nint findData, int searchOp, nint searchFilter, int additionalFlags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint findFile);
#pragma warning restore SYSLIB1054
}
