using FileDetective.Core;

namespace FileDetective.Tests;

public class DriveDiscoveryTests
{
    private sealed class FakeDrives : IDriveSource
    {
        public List<DriveSnapshot> Drives { get; } = [];

        public Dictionary<string, string?> Unc { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string?> Labels { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Throwing { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> LabelRequests { get; } = [];

        public IReadOnlyList<DriveSnapshot> GetDrives() => Drives;

        public string? GetNetworkPath(string driveLetter)
        {
            if (Throwing.Contains(driveLetter))
            {
                throw new IOException("mapping unavailable");
            }

            return Unc.GetValueOrDefault(driveLetter);
        }

        public string? GetLabel(string driveRoot)
        {
            LabelRequests.Add(driveRoot);
            return Labels.GetValueOrDefault(driveRoot);
        }
    }

    private static FakeDrives Sample()
    {
        var f = new FakeDrives();
        f.Drives.AddRange(
        [
            new DriveSnapshot(@"P:\", DriveType.Network),
            new DriveSnapshot(@"D:\", DriveType.CDRom),
            new DriveSnapshot(@"C:\", DriveType.Fixed),
            new DriveSnapshot(@"E:\", DriveType.Removable),
            new DriveSnapshot(@"R:\", DriveType.Ram),
            new DriveSnapshot(@"Z:\", DriveType.NoRootDirectory),
        ]);
        f.Unc["P:"] = @"\\srv\share\Projekte";
        f.Labels[@"C:\"] = "System";
        f.Labels[@"E:\"] = "  ";
        return f;
    }

    [Fact]
    public void Discover_ListsLocalRemovableAndNetwork_OrderedByLetter_WithoutOtherTypes()
    {
        var drives = new DriveDiscovery(Sample()).Discover();

        Assert.Equal(["C:", "E:", "P:"], drives.Select(d => d.Letter));
        Assert.Equal([RootKind.Local, RootKind.Removable, RootKind.Network], drives.Select(d => d.Kind));
        Assert.Equal([@"C:\", @"E:\", @"P:\"], drives.Select(d => d.Path));
    }

    [Fact]
    public void Discover_NetworkDriveHasUncPath_AndIsNeverAskedForALabel()
    {
        var f = Sample();
        var p = new DriveDiscovery(f).Discover().Single(d => d.Letter == "P:");

        Assert.Equal(@"\\srv\share\Projekte", p.UncPath);
        Assert.Null(p.Label);
        Assert.DoesNotContain(@"P:\", f.LabelRequests);
    }

    [Fact]
    public void Discover_LabelsOnlyForLocalAndRemovable_BlankLabelIsNull()
    {
        var drives = new DriveDiscovery(Sample()).Discover();
        Assert.Equal("System", drives.Single(d => d.Letter == "C:").Label);
        Assert.Null(drives.Single(d => d.Letter == "E:").Label);
        Assert.Null(drives.Single(d => d.Letter == "C:").UncPath);
    }

    [Fact]
    public void Discover_FailingMapping_StillListsTheDriveWithoutUnc()
    {
        var f = Sample();
        f.Throwing.Add("P:");
        var p = new DriveDiscovery(f).Discover().Single(d => d.Letter == "P:");
        Assert.Null(p.UncPath);
        Assert.Equal(RootKind.Network, p.Kind);
    }

    [Fact]
    public void Discover_NoDrives_IsEmpty()
        => Assert.Empty(new DriveDiscovery(new FakeDrives()).Discover());

    [Fact]
    public void Discover_ToCandidate_CarriesPathUncAndKind()
    {
        var candidate = new DriveDiscovery(Sample()).Discover().Single(d => d.Letter == "P:").ToCandidate();
        Assert.Equal(new LocationCandidate(@"P:\", @"\\srv\share\Projekte", RootKind.Network), candidate);
    }

    [Theory]
    [InlineData(@"C:\Users\anna", RootKind.Local)]
    [InlineData(@"c:\", RootKind.Local)]
    [InlineData(@"E:\Fotos", RootKind.Removable)]
    [InlineData(@"\\other\share\x", RootKind.Network)]
    [InlineData(@"X:\unknown\drive", RootKind.Local)]
    public void Describe_ClassifiesByDriveOrUnc(string path, RootKind expected)
        => Assert.Equal(expected, new DriveDiscovery(Sample()).Describe(path)!.Kind);

    [Fact]
    public void Describe_MappedDrivePath_GetsUncForm()
    {
        var d = new DriveDiscovery(Sample());
        var sub = d.Describe(@"p:\2026\Q1\")!;
        Assert.Equal(@"P:\2026\Q1", sub.Path);
        Assert.Equal(@"\\srv\share\Projekte\2026\Q1", sub.UncPath);
        Assert.Equal(RootKind.Network, sub.Kind);

        var root = d.Describe(@"P:\")!;
        Assert.Equal(@"\\srv\share\Projekte", root.UncPath);
    }

    [Fact]
    public void Describe_TypedUnc_IsNetwork_WithoutUncAlias()
    {
        var c = new DriveDiscovery(Sample()).Describe(@"\\srv\share\Projekte\2026")!;
        Assert.Equal(@"\\srv\share\Projekte\2026", c.Path);
        Assert.Null(c.UncPath);
        Assert.Equal(RootKind.Network, c.Kind);
    }

    [Fact]
    public void Describe_ThenNestingRule_FindsMappedDriveInsideTypedShare()
    {
        var d = new DriveDiscovery(Sample());
        var share = d.Describe(@"\\srv\share")!;
        var mapped = d.Describe(@"P:\")!;
        Assert.Equal(LocationRelation.FirstContainsSecond, LocationRules.Compare(share, mapped));
        Assert.Single(LocationRules.KeepOutermost([mapped, share]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData(@"\\server")]
    public void Describe_InvalidPath_IsNull(string path)
        => Assert.Null(new DriveDiscovery(Sample()).Describe(path));

    [Fact]
    public void RealSystem_FindsAtLeastTheSystemDrive_WithoutHanging()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var drives = new DriveDiscovery().Discover();
        Assert.Contains(drives, d => d.Kind == RootKind.Local);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30));
    }
}
