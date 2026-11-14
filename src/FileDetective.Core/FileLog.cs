using System.Globalization;
using System.Text;

namespace FileDetective.Core;

/// <summary>
/// Simple text log in the data folder (PLAN.md 5.2, 7.4). One file; when the next line would take it over the size limit
/// (default 1 MiB), the file becomes <c>&lt;name&gt;.old</c> (an older <c>.old</c> is replaced) and a new file starts,
/// so the log never grows beyond about twice the limit. Thread-safe. Never throws: a log that cannot be written is
/// silently skipped, logging must never break the program.
/// </summary>
public sealed class FileLog
{
    /// <summary>Default size limit of the current file: 1 MiB.</summary>
    public const long DefaultMaxBytes = 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly long _maxBytes;
    private readonly TimeProvider _clock;
    private long _size = -1; // -1: not read from the file system yet
    private bool _directoryReady;

    /// <param name="path">The log file; null or empty gives a log that writes nothing.</param>
    /// <param name="maxBytes">Size limit of the current file. A single line longer than the limit is still written.</param>
    /// <param name="clock">Source of the time stamps (local time); default the system clock.</param>
    public FileLog(string? path, long maxBytes = DefaultMaxBytes, TimeProvider? clock = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _maxBytes = maxBytes;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// The log of the running program. Writes nothing until <see cref="Initialize"/> was called, so code that logs
    /// never needs a null check (and tests never write a log file by accident).
    /// </summary>
    public static FileLog Shared { get; private set; } = new(null);

    /// <summary>The file of the previous log (<c>.old</c> next to the current file), or null for a log without file.</summary>
    public string? OldPath => _path is null ? null : _path + ".old";

    public string? Path => _path;

    /// <summary>Makes a log with the given file the shared one.</summary>
    public static FileLog Initialize(string path, long maxBytes = DefaultMaxBytes)
    {
        var log = new FileLog(path, maxBytes);
        Shared = log;
        return log;
    }

    public void Info(string message) => Write("INFO ", message);

    public void Error(string message, Exception? exception = null)
        => Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private void Write(string level, string message)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            string line = string.Create(
                CultureInfo.InvariantCulture,
                $"{_clock.GetLocalNow():yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            byte[] bytes = Utf8.GetBytes(line);
            lock (_gate)
            {
                EnsureDirectory();
                if (_size < 0)
                {
                    _size = File.Exists(_path) ? new FileInfo(_path).Length : 0;
                }

                // Rotate before the line that would pass the limit; an empty file is never rotated (a huge single
                // line is written anyway instead of rotating forever).
                if (_size > 0 && _size + bytes.Length > _maxBytes)
                {
                    Rotate();
                }

                using (var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Write(bytes);
                }

                _size += bytes.Length;
            }
        }
        catch (Exception)
        {
            // Logging must never break the program. Forget the cached size: the file may have changed under us.
            lock (_gate)
            {
                _size = -1;
            }
        }
    }

    private void EnsureDirectory()
    {
        if (_directoryReady)
        {
            return;
        }

        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path!));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _directoryReady = true;
    }

    private void Rotate()
    {
        string old = _path + ".old";
        try
        {
            File.Move(_path!, old, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The old file may be open in an editor: then the current file is simply started anew.
            File.Delete(_path!);
        }

        _size = 0;
    }
}
