using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>
/// Stand-in for <see cref="ILocalFileAccess"/> that counts every call per path: probes (no open), identity queries (an
/// open without reading) and opens for reading, plus the bytes read.
/// </summary>
internal sealed class FakeLocalFiles : ILocalFileAccess
{
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private ulong _nextId = 1;

    public static readonly long BaseTicks = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).Ticks;

    public Dictionary<string, int> Probes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> Identities { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> Opens { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long BytesRead { get; private set; }

    public int TotalCalls => Probes.Values.Sum() + Identities.Values.Sum() + Opens.Values.Sum();

    public int OpenCount => Opens.Values.Sum();

    public int OpensOf(string path) => Opens.GetValueOrDefault(path);

    /// <summary>Any call at all (probe, identity, read) for this path.</summary>
    public int CallsOf(string path) => Probes.GetValueOrDefault(path) + Identities.GetValueOrDefault(path) + Opens.GetValueOrDefault(path);

    public void ResetCounters()
    {
        Probes.Clear();
        Identities.Clear();
        Opens.Clear();
        BytesRead = 0;
    }

    public FakeLocalFiles Add(string path, byte[] content, long? ticks = null, FileAttributes attributes = FileAttributes.Archive, uint tag = 0)
    {
        _files[path] = new FakeFile(content, ticks ?? BaseTicks, attributes, new FileIdentity(7, _nextId++, 0), tag);
        return this;
    }

    /// <summary>A second name of the same file (hard link): same content, same identity.</summary>
    public FakeLocalFiles HardLink(string newPath, string existing)
    {
        _files[newPath] = _files[existing];
        return this;
    }

    public void Change(string path, byte[]? content = null, long? ticks = null, FileAttributes? attributes = null)
    {
        var f = _files[path];
        _files[path] = f with
        {
            Content = content ?? f.Content,
            Ticks = ticks ?? f.Ticks,
            Attributes = attributes ?? f.Attributes,
        };
    }

    public void Remove(string path) => _files.Remove(path);

    public LocalFileFacts? Probe(string path)
    {
        Count(Probes, path);
        return _files.TryGetValue(path, out var f) ? new LocalFileFacts(f.Content.Length, f.Ticks, f.Attributes, f.Tag) : null;
    }

    public FileIdentity? GetIdentity(string path)
    {
        Count(Identities, path);
        return _files.TryGetValue(path, out var f) ? f.Id : null;
    }

    public Stream OpenRead(string path)
    {
        Count(Opens, path);
        if (!_files.TryGetValue(path, out var f))
        {
            throw new FileNotFoundException(path);
        }

        return new CountingStream(f.Content, n => BytesRead += n);
    }

    private static void Count(Dictionary<string, int> counter, string path) => counter[path] = counter.GetValueOrDefault(path) + 1;

    private sealed record FakeFile(byte[] Content, long Ticks, FileAttributes Attributes, FileIdentity Id, uint Tag);

    private sealed class CountingStream(byte[] content, Action<int> counted) : MemoryStream(content, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = base.Read(buffer, offset, count);
            counted(n);
            return n;
        }
    }
}

/// <summary>Drives for the duplicate tests: R: fixed, U: removable, N: network drive of \\server\share.</summary>
internal sealed class DuplicateDriveSource : IDriveSource
{
    public IReadOnlyList<DriveSnapshot> GetDrives()
        => [new DriveSnapshot(@"R:\", DriveType.Fixed), new DriveSnapshot(@"U:\", DriveType.Removable), new DriveSnapshot(@"N:\", DriveType.Network)];

    public string? GetNetworkPath(string driveLetter) => driveLetter.StartsWith('N') ? @"\\server\share" : null;

    public string? GetLabel(string driveRoot) => null;
}

/// <summary>Records moves instead of touching the real Recycle Bin.</summary>
internal sealed class RecordingBin : IRecycleBin
{
    public List<string> Files { get; } = [];

    public Action<string>? OnMove { get; set; }

    public void MoveFile(string path)
    {
        OnMove?.Invoke(path);
        Files.Add(path);
    }

    public void MoveDirectory(string path) => throw new InvalidOperationException("Duplicates never move folders.");
}

internal sealed class SetProbe(params string[] existing) : IPathProbe
{
    public HashSet<string> Existing { get; } = new(existing, StringComparer.OrdinalIgnoreCase);

    public bool Exists(string path) => Existing.Contains(path.TrimEnd('\\')) || Existing.Contains(path);
}

internal static class Content
{
    /// <summary>Reproducible pseudo-random bytes.</summary>
    public static byte[] Random(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A copy with one byte changed at <paramref name="position"/>.</summary>
    public static byte[] ChangedAt(byte[] source, int position)
    {
        var copy = (byte[])source.Clone();
        copy[position] ^= 0xFF;
        return copy;
    }
}
