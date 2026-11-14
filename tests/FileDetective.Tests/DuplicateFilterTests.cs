using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Hiding program and database files in the duplicates window (PLAN.md 11.2).</summary>
public class DuplicateFilterTests
{
    private static int _nextId = 1;

    private static DuplicateGroup Group(params string[] paths)
        => new(paths.Select(p => new DuplicateFile(_nextId++, 1, p, 2_000_000, 0, canRecycle: true)), verified: true);

    [Theory]
    [InlineData(@"C:\Apps\a\System.Data.SQLite.dll", true)]
    [InlineData(@"C:\Apps\tool.EXE", true)]
    [InlineData(@"C:\Users\anna\cache.sqlite", true)]
    [InlineData(@"C:\Users\anna\data.db-wal", true)]
    [InlineData(@"C:\Code\App\bin\Debug\logo.png", true)]
    [InlineData(@"C:\Code\App\obj\x.txt", true)]
    [InlineData(@"C:\Code\App\publish\readme.md", true)]
    [InlineData(@"C:\Users\anna\.nuget\packages\x\1.0\lib.xml", true)]
    [InlineData(@"C:\Users\anna\Bilder\urlaub.jpg", false)]
    [InlineData(@"C:\Users\anna\Dokumente\Vertrag.pdf", false)]
    [InlineData(@"C:\Users\anna\binder\notes.txt", false)]  // "binder" is not "bin"
    [InlineData(@"C:\Users\anna\Bilder\bin", false)]        // a file named bin, not a folder
    [InlineData(@"C:\Users\anna\database.dbx", false)]
    public void IsTechnical_File(string path, bool expected)
        => Assert.Equal(expected, DuplicateFilter.IsTechnical(path));

    [Fact]
    public void Group_IsTechnicalOnlyIfEveryFileIs()
    {
        Assert.True(DuplicateFilter.IsTechnical(Group(@"C:\a\x.dll", @"C:\b\x.dll")));
        Assert.False(DuplicateFilter.IsTechnical(Group(@"C:\Code\App\bin\Debug\logo.png", @"C:\Users\anna\Bilder\logo.png")));
    }

    [Fact]
    public void Apply_HidesTechnicalGroupsAndCountsThem()
    {
        var photos = Group(@"C:\p\a.jpg", @"C:\q\a.jpg");
        var dlls = Group(@"C:\a\x.dll", @"C:\b\x.dll");
        var dbs = Group(@"C:\a\c.sqlite", @"C:\b\c.sqlite");

        var (visible, hidden) = DuplicateFilter.Apply([photos, dlls, dbs], hideTechnical: true);
        Assert.Equal([photos], visible);
        Assert.Equal(2, hidden);

        var (all, none) = DuplicateFilter.Apply([photos, dlls, dbs], hideTechnical: false);
        Assert.Equal(3, all.Count);
        Assert.Equal(0, none);
    }

    [Fact]
    public void Apply_ClearsMarksOfHiddenGroups()
    {
        // Marked while shown, then hidden: the Recycle Bin must never get a file the user cannot see.
        var dlls = Group(@"C:\a\x.dll", @"C:\b\x.dll", @"C:\c\x.dll");
        var photos = Group(@"C:\p\a.jpg", @"C:\q\a.jpg");
        Assert.True(dlls.SetMarked(dlls.Files[0], true));
        Assert.True(photos.SetMarked(photos.Files[0], true));

        DuplicateFilter.Apply([dlls, photos], hideTechnical: true);

        Assert.Equal(0, dlls.MarkedCount);
        Assert.Equal(1, photos.MarkedCount);
    }

    [Fact]
    public void Setting_HideTechnicalDuplicates_DefaultsToTrueAndRoundTrips()
    {
        Assert.True(new AppSettings().HideTechnicalDuplicates);
        string json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { HideTechnicalDuplicates = false });
        Assert.Contains("\"hideTechnicalDuplicates\":false", json, StringComparison.Ordinal);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.HideTechnicalDuplicates);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.HideTechnicalDuplicates);
    }
}
