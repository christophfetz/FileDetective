using FileDetective.Core;

namespace FileDetective.Tests;

public class LocationRulesTests
{
    private static LocationCandidate Loc(string path, string? unc = null, RootKind kind = RootKind.Local) => new(path, unc, kind);

    [Theory]
    [InlineData(@"C:\Daten", @"C:\Daten")]
    [InlineData(@"c:\daten\", @"C:\daten")]
    [InlineData(@"C:\Daten\\Sub\", @"C:\Daten\Sub")]
    [InlineData("C:/Daten/Sub", @"C:\Daten\Sub")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData("C:", @"C:\")]
    [InlineData(@"c:/", @"C:\")]
    [InlineData(@"  D:\x  ", @"D:\x")]
    [InlineData(@"C:\a\.\b\..\c", @"C:\a\c")]
    [InlineData(@"\\Server\Share", @"\\Server\Share")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData(@"\\server\share\\x\y\", @"\\server\share\x\y")]
    [InlineData("//server/share/x", @"\\server\share\x")]
    public void Normalize_GivesOneForm(string input, string expected)
        => Assert.Equal(expected, LocationRules.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Daten")]
    [InlineData(@"\Daten")]
    [InlineData("C:Daten")]
    [InlineData(@"\\server")]
    [InlineData(@"\\server\")]
    [InlineData(@"\\?\C:\x")]
    [InlineData(@"C:\a\..\..")]
    [InlineData(@"C:\a|b")]
    [InlineData(@"C:\a*")]
    [InlineData(@"C:\a:b")]
    [InlineData(@"1:\x")]
    public void Normalize_RefusesWhatIsNoAbsoluteLocation(string? input)
        => Assert.Null(LocationRules.Normalize(input));

    [Theory]
    [InlineData(@"C:\Daten", @"C:\Daten", RootKind.Local, LocationRelation.Same)]
    [InlineData(@"C:\Daten", @"c:\DATEN\", RootKind.Local, LocationRelation.Same)]
    [InlineData(@"C:\Daten", @"C:\Daten\Sub", RootKind.Local, LocationRelation.FirstContainsSecond)]
    [InlineData(@"C:\Daten", @"C:\daten\sub\tief", RootKind.Local, LocationRelation.FirstContainsSecond)]
    [InlineData(@"C:\Daten\Sub", @"C:\Daten", RootKind.Local, LocationRelation.FirstInsideSecond)]
    // path boundary: a prefix of the text is not a parent folder
    [InlineData(@"C:\Daten", @"C:\Daten2", RootKind.Local, LocationRelation.Unrelated)]
    [InlineData(@"C:\Daten2", @"C:\Daten", RootKind.Local, LocationRelation.Unrelated)]
    [InlineData(@"C:\Daten", @"C:\Daten Alt\x", RootKind.Local, LocationRelation.Unrelated)]
    [InlineData(@"C:\Daten", @"C:\Dat", RootKind.Local, LocationRelation.Unrelated)]
    // drive root
    [InlineData(@"C:\", @"C:\Daten", RootKind.Local, LocationRelation.FirstContainsSecond)]
    [InlineData(@"C:\", @"C:\", RootKind.Local, LocationRelation.Same)]
    [InlineData(@"C:", @"c:\x\y", RootKind.Local, LocationRelation.FirstContainsSecond)]
    [InlineData(@"C:\", @"D:\", RootKind.Local, LocationRelation.Unrelated)]
    [InlineData(@"D:\x", @"C:\x", RootKind.Local, LocationRelation.Unrelated)]
    // UNC
    [InlineData(@"\\srv\share", @"\\srv\share\Projekte", RootKind.Network, LocationRelation.FirstContainsSecond)]
    [InlineData(@"\\SRV\Share\", @"\\srv\share", RootKind.Network, LocationRelation.Same)]
    [InlineData(@"\\srv\share", @"\\srv\share2", RootKind.Network, LocationRelation.Unrelated)]
    [InlineData(@"\\srv\share\a", @"\\srv\share\ab", RootKind.Network, LocationRelation.Unrelated)]
    [InlineData(@"\\srv\share\a\b", @"\\srv\share\a", RootKind.Network, LocationRelation.FirstInsideSecond)]
    [InlineData(@"\\srv1\share", @"\\srv2\share", RootKind.Network, LocationRelation.Unrelated)]
    // a UNC path never relates to a plain local drive path by text
    [InlineData(@"C:\share", @"\\srv\share", RootKind.Network, LocationRelation.Unrelated)]
    public void Compare_UsesPathBoundaryAndIgnoresCase(string first, string second, RootKind kind, LocationRelation expected)
    {
        Assert.Equal(expected, LocationRules.Compare(Loc(first, null, kind), Loc(second, null, kind)));
        var mirrored = expected switch
        {
            LocationRelation.FirstContainsSecond => LocationRelation.FirstInsideSecond,
            LocationRelation.FirstInsideSecond => LocationRelation.FirstContainsSecond,
            _ => expected,
        };
        Assert.Equal(mirrored, LocationRules.Compare(Loc(second, null, kind), Loc(first, null, kind)));
    }

    [Fact]
    public void Compare_MappedDriveAndItsUncPath_AreTheSamePlace()
    {
        var mapped = Loc(@"P:\Projekte", @"\\srv\share\Projekte", RootKind.Network);
        Assert.Equal(LocationRelation.Same, LocationRules.Compare(mapped, Loc(@"\\SRV\share\projekte\", null, RootKind.Network)));
        Assert.Equal(
            LocationRelation.FirstContainsSecond,
            LocationRules.Compare(mapped, Loc(@"\\srv\share\Projekte\2026", null, RootKind.Network)));
        Assert.Equal(
            LocationRelation.FirstInsideSecond,
            LocationRules.Compare(mapped, Loc(@"\\srv\share", null, RootKind.Network)));
        Assert.Equal(
            LocationRelation.Unrelated,
            LocationRules.Compare(mapped, Loc(@"\\srv\share\Projekte2", null, RootKind.Network)));

        // two letters for the same share
        var other = Loc(@"Q:\", @"\\srv\share", RootKind.Network);
        Assert.Equal(LocationRelation.FirstInsideSecond, LocationRules.Compare(mapped, other));
        Assert.Equal(LocationRelation.FirstContainsSecond, LocationRules.Compare(other, mapped));
    }

    [Fact]
    public void KeepOutermost_DropsNestedLocations_InAnyOrder()
    {
        var a = Loc(@"C:\Daten\Sub");
        var b = Loc(@"C:\Daten");
        var c = Loc(@"C:\Daten2");
        var d = Loc(@"D:\x");

        Assert.Equal([b, c, d], LocationRules.KeepOutermost([a, b, c, d]));
        Assert.Equal([b, c, d], LocationRules.KeepOutermost([b, a, c, d]));
        Assert.Equal([c, d, b], LocationRules.KeepOutermost([c, d, a, b]));
    }

    [Fact]
    public void KeepOutermost_DriveRootSwallowsEverythingOnThatDrive()
    {
        var root = Loc(@"C:\");
        var user = Loc(@"C:\Users\anna");
        var other = Loc(@"D:\Daten");
        Assert.Equal([root, other], LocationRules.KeepOutermost([user, root, other]));
    }

    [Fact]
    public void KeepOutermost_SamePlaceTwice_KeepsTheFirst()
    {
        var first = Loc(@"C:\Daten");
        var second = Loc(@"c:\daten\");
        var kept = LocationRules.KeepOutermost([first, second]);
        Assert.Single(kept);
        Assert.Same(first, kept[0]);

        // a letter and the UNC path of the same target
        var letter = Loc(@"P:\x", @"\\srv\s\x", RootKind.Network);
        var unc = Loc(@"\\srv\s\x", null, RootKind.Network);
        Assert.Same(unc, Assert.Single(LocationRules.KeepOutermost([unc, letter])));
    }

    [Fact]
    public void KeepOutermost_UncShareSwallowsMappedSubfolder()
    {
        var share = Loc(@"\\srv\share", null, RootKind.Network);
        var mapped = Loc(@"P:\Projekte", @"\\srv\share\Projekte", RootKind.Network);
        var kept = LocationRules.KeepOutermost([mapped, share]);
        Assert.Same(share, Assert.Single(kept));
    }

    [Fact]
    public void KeepOutermost_EmptyAndInvalidPaths()
    {
        Assert.Empty(LocationRules.KeepOutermost([]));
        Assert.Empty(LocationRules.KeepOutermost([Loc("relative\\path")]));
    }

    [Fact]
    public void FindOuter_NamesTheCoveringLocation()
    {
        var list = new[] { Loc(@"C:\Daten\Sub"), Loc(@"C:\Daten"), Loc(@"C:\Daten2") };
        Assert.Same(list[1], LocationRules.FindOuter(list, 0));
        Assert.Null(LocationRules.FindOuter(list, 1));
        Assert.Null(LocationRules.FindOuter(list, 2));
    }

    private static RootInfo Root(byte id, string path, string? unc = null, RootKind kind = RootKind.Local)
        => new(id, path, unc, kind, true, null, null, ScanStatus.Never, 0, 0, id);

    [Fact]
    public void Check_FreeSameInsideContains()
    {
        RootInfo[] existing = [Root(1, @"C:\Daten"), Root(2, @"D:\Foto\2025"), Root(3, @"D:\Foto\2026")];

        Assert.Equal(LocationCheckResult.Free, LocationRules.Check(Loc(@"C:\Daten2"), existing).Result);
        Assert.Equal(LocationCheckResult.Free, LocationRules.Check(Loc(@"E:\x"), []).Result);

        var same = LocationRules.Check(Loc(@"c:\daten\"), existing);
        Assert.Equal(LocationCheckResult.AlreadyExists, same.Result);
        Assert.Equal(1, Assert.Single(same.Related).Id);

        var inside = LocationRules.Check(Loc(@"C:\Daten\Sub"), existing);
        Assert.Equal(LocationCheckResult.InsideExisting, inside.Result);
        Assert.Equal(1, Assert.Single(inside.Related).Id);

        var contains = LocationRules.Check(Loc(@"D:\Foto"), existing);
        Assert.Equal(LocationCheckResult.ContainsExisting, contains.Result);
        Assert.Equal([2, 3], contains.Related.Select(r => (int)r.Id));
    }

    [Fact]
    public void Check_UncAndLetterOfTheSameShare()
    {
        RootInfo[] existing = [Root(1, @"P:\Projekte", @"\\srv\share\Projekte", RootKind.Network)];
        Assert.Equal(
            LocationCheckResult.AlreadyExists,
            LocationRules.Check(Loc(@"\\srv\share\Projekte", null, RootKind.Network), existing).Result);
        Assert.Equal(
            LocationCheckResult.InsideExisting,
            LocationRules.Check(Loc(@"\\srv\share\Projekte\Alt", null, RootKind.Network), existing).Result);
        Assert.Equal(
            LocationCheckResult.ContainsExisting,
            LocationRules.Check(Loc(@"\\srv\share", null, RootKind.Network), existing).Result);
        Assert.Equal(
            LocationCheckResult.Free,
            LocationRules.Check(Loc(@"\\srv\share\Projekte2", null, RootKind.Network), existing).Result);
    }

    [Theory]
    [InlineData(@"C:\Daten", @"C:\Daten\x", true)]
    [InlineData(@"C:\Daten", @"C:\Daten", true)]
    [InlineData(@"C:\Daten", @"C:\Daten2", false)]
    [InlineData(@"C:\", @"C:\x", true)]
    [InlineData(@"C:\x", @"C:\", false)]
    public void ContainsOrEquals_AtPathBoundary(string outer, string inner, bool expected)
        => Assert.Equal(expected, LocationRules.ContainsOrEquals(outer, inner));
}
