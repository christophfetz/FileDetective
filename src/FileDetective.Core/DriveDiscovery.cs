using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace FileDetective.Core;

/// <summary>A drive as the operating system lists it, without any query that could touch a share.</summary>
/// <param name="Name">Root of the drive, <c>C:\</c>.</param>
/// <param name="Type">Kind of drive.</param>
public sealed record DriveSnapshot(string Name, DriveType Type);

/// <summary>Source of drive information; the real one asks Windows, tests use a stand-in.</summary>
public interface IDriveSource
{
    /// <summary>All drives with letter and type. Must not open anything on a network drive.</summary>
    IReadOnlyList<DriveSnapshot> GetDrives();

    /// <summary>UNC path a network drive (<c>P:</c>) is connected to, or null if unknown. Must not touch the network.</summary>
    string? GetNetworkPath(string driveLetter);

    /// <summary>Volume label of a local or removable drive, or null if unknown. Never called for network drives.</summary>
    string? GetLabel(string driveRoot);
}

/// <summary>A drive offered as a location.</summary>
public sealed record DiscoveredDrive(string Path, string Letter, RootKind Kind, string? UncPath, string? Label)
{
    public LocationCandidate ToCandidate() => new(Path, UncPath, Kind);
}

/// <summary>
/// Finds the drives a user can choose as locations (PLAN.md 8.5): local disks, removable media and connected network
/// drives with letter and UNC path. Nothing here opens a file or lists a folder on a share; the UNC path comes from
/// the local connection table (<c>WNetGetConnection</c>), and sizes or free space are never asked (on a dead share
/// <c>DriveInfo.IsReady</c> can hang for a long time). The caller should still run <see cref="Discover"/> on a background thread.
/// </summary>
public sealed class DriveDiscovery
{
    private readonly IDriveSource _source;

    public DriveDiscovery(IDriveSource? source = null)
    {
        _source = source ?? SystemDriveSource.Instance;
    }

    /// <summary>Local, removable and network drives ordered by letter. CD/DVD drives, RAM disks and unknown types are left out.</summary>
    public IReadOnlyList<DiscoveredDrive> Discover()
    {
        var result = new List<DiscoveredDrive>();
        foreach (var drive in _source.GetDrives())
        {
            var kind = KindOf(drive.Type);
            if (kind is null || Normalize(drive.Name) is not { } root)
            {
                continue;
            }

            string letter = root[..2];
            string? unc = null;
            string? label = null;
            try
            {
                if (kind == RootKind.Network)
                {
                    unc = LocationRules.Normalize(_source.GetNetworkPath(letter));
                }
                else
                {
                    label = _source.GetLabel(root);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
            {
                // The extra information is optional: the drive is still offered.
            }

            result.Add(new DiscoveredDrive(root, letter, kind.Value, unc, string.IsNullOrWhiteSpace(label) ? null : label.Trim()));
        }

        return [.. result.OrderBy(d => d.Letter, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Describes a path the user chose or typed: normalized, kind (UNC path = network; drive letter = kind of that
    /// drive, local if unknown) and, for a mapped network drive, its UNC form. Returns null if the text is not an
    /// absolute drive or UNC path. Does not touch the file system.
    /// </summary>
    public LocationCandidate? Describe(string? rawPath)
    {
        string? path = LocationRules.Normalize(rawPath);
        if (path is null)
        {
            return null;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new LocationCandidate(path, null, RootKind.Network);
        }

        var type = _source.GetDrives().FirstOrDefault(d => Normalize(d.Name)?.StartsWith(path[..2], StringComparison.OrdinalIgnoreCase) == true)?.Type;
        var kind = type is null ? RootKind.Local : KindOf(type.Value) ?? RootKind.Local;
        string? unc = null;
        if (kind == RootKind.Network)
        {
            try
            {
                if (LocationRules.Normalize(_source.GetNetworkPath(path[..2])) is { } share)
                {
                    unc = path.Length == 3 ? share : share.TrimEnd('\\') + path[2..];
                }
            }
            catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
            {
                // Without the UNC form only the comparison with a typed UNC path is less precise.
            }
        }

        return new LocationCandidate(path, unc, kind);
    }

    private static RootKind? KindOf(DriveType type) => type switch
    {
        DriveType.Fixed => RootKind.Local,
        DriveType.Removable => RootKind.Removable,
        DriveType.Network => RootKind.Network,
        _ => null,
    };

    private static string? Normalize(string name)
        => name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':' ? char.ToUpperInvariant(name[0]) + @":\" : null;
}

/// <summary>The real drive source: <see cref="DriveInfo.GetDrives"/> (letter and type only) and <c>WNetGetConnection</c>.</summary>
internal sealed class SystemDriveSource : IDriveSource
{
    private const int NoError = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorConnectionUnavail = 1201;
    private static readonly TimeSpan LabelTimeout = TimeSpan.FromSeconds(1.5);

    public static SystemDriveSource Instance { get; } = new();

    public IReadOnlyList<DriveSnapshot> GetDrives()
    {
        // Name and DriveType do not read the volume (GetDriveType only looks at local tables).
        return [.. DriveInfo.GetDrives().Select(d => new DriveSnapshot(d.Name, d.DriveType))];
    }

    public string? GetNetworkPath(string driveLetter)
    {
        int length = 512;
        var buffer = new StringBuilder(length);
        int rc = WNetGetConnection(driveLetter, buffer, ref length);
        if (rc == ErrorMoreData)
        {
            buffer = new StringBuilder(length);
            rc = WNetGetConnection(driveLetter, buffer, ref length);
        }

        // A remembered connection that is currently not connected still reports its remote name.
        return rc is NoError or ErrorConnectionUnavail && buffer.Length > 0 ? buffer.ToString() : null;
    }

    public string? GetLabel(string driveRoot)
    {
        // The label needs a read of the volume; an empty card reader or a sleeping disk can be slow: wait only briefly.
        var task = Task.Run(() => new DriveInfo(driveRoot).VolumeLabel);
        try
        {
            return task.Wait(LabelTimeout) ? task.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}
