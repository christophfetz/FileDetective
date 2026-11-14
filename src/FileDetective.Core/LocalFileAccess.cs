using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileDetective.Core;

/// <summary>What the directory listing of the parent says about a file right now (no open of the file itself).</summary>
/// <param name="LastWriteUtcTicks">Last write time, UTC, in ticks (100 ns).</param>
/// <param name="ReparseTag">The reparse tag if the file is a reparse point, else 0.</param>
public readonly record struct LocalFileFacts(long Size, long LastWriteUtcTicks, FileAttributes Attributes, uint ReparseTag);

/// <summary>Volume serial number and file id: two names with the same identity are hard links to one file.</summary>
public readonly record struct FileIdentity(ulong Volume, ulong IdLow, ulong IdHigh);

/// <summary>
/// Access to local files for the duplicate check (stage 10, PLAN.md 11.2). Real one: <see cref="SystemLocalFileAccess"/>;
/// tests use a stand-in that counts every call. Only ever used for files on local drives: shares are never handed to it.
/// </summary>
public interface ILocalFileAccess
{
    /// <summary>Current size, time, attributes and reparse tag from the listing of the parent folder; never opens the file. Null if it is gone.</summary>
    LocalFileFacts? Probe(string path);

    /// <summary>Volume and file id. Opens the file for its attributes only, without reading content. Null if that fails.</summary>
    FileIdentity? GetIdentity(string path);

    /// <summary>Opens the file for reading its content (shared, sequential). The caller disposes the stream.</summary>
    Stream OpenRead(string path);
}

/// <summary>Real <see cref="ILocalFileAccess"/> on top of the Windows API.</summary>
public sealed class SystemLocalFileAccess : ILocalFileAccess
{
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int Win32FindDataSize = 592;
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const int FileIdInfoClass = 18;
    private const int FileIdInfoSize = 24;

    public static SystemLocalFileAccess Instance { get; } = new();

    public LocalFileFacts? Probe(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        nint buffer = Marshal.AllocHGlobal(Win32FindDataSize);
        try
        {
            nint handle = FindFirstFileExW(LongPath(path), FindExInfoBasic, buffer, FindExSearchNameMatch, 0, 0);
            if (handle == -1)
            {
                return null;
            }

            FindClose(handle);
            var attributes = (FileAttributes)Marshal.ReadInt32(buffer, 0);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                return null;
            }

            long lastWrite = ((long)(uint)Marshal.ReadInt32(buffer, 24) << 32) | (uint)Marshal.ReadInt32(buffer, 20);
            long size = ((long)(uint)Marshal.ReadInt32(buffer, 28) << 32) | (uint)Marshal.ReadInt32(buffer, 32);
            uint tag = (attributes & FileAttributes.ReparsePoint) != 0 ? unchecked((uint)Marshal.ReadInt32(buffer, 36)) : 0;
            return new LocalFileFacts(size, DateTime.FromFileTimeUtc(lastWrite).Ticks, attributes, tag);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public FileIdentity? GetIdentity(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using SafeFileHandle handle = CreateFileW(LongPath(path), FileReadAttributes, FileShareAll, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(FileIdInfoSize);
        try
        {
            if (!GetFileInformationByHandleEx(handle, FileIdInfoClass, buffer, FileIdInfoSize))
            {
                return null;
            }

            return new FileIdentity(
                unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 16)));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public Stream OpenRead(string path)
        => new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 0,
        });

    private static string LongPath(string path)
    {
        string p = path.TrimEnd('\\', '/');
        if (p.Length >= 248 && !p.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            p = p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
        }

        return p;
    }

#pragma warning disable SYSLIB1054 // LibraryImport would need AllowUnsafeBlocks; DllImport is fine for these few calls.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint FindFirstFileExW(
        string fileName, int infoLevel, nint findData, int searchOp, nint searchFilter, int additionalFlags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint findFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, nint info, int size);
#pragma warning restore SYSLIB1054
}

/// <summary>
/// Runs work on its own thread with the lowest priority and in Windows background mode, which also lowers the priority of
/// its disk reads (<c>THREAD_MODE_BACKGROUND_BEGIN</c>), so reading files does not slow down the computer (PLAN.md 11.2).
/// </summary>
public static class LowPriorityWork
{
    private const int ThreadModeBackgroundBegin = 0x0001_0000;
    private const int ThreadModeBackgroundEnd = 0x0002_0000;

    public static Task<T> Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            bool background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
            try
            {
                completion.SetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                completion.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                if (background)
                {
                    SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
                }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = "FileDetective low priority work",
        };
        thread.Start();
        return completion.Task;
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(nint thread, int priority);
#pragma warning restore SYSLIB1054
}
