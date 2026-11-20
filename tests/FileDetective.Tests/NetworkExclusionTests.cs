using System.Text.Json;
using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Folder names skipped on network locations only (PLAN.md 7.3, default <c>bin</c> and <c>obj</c>).</summary>
public class NetworkExclusionTests
{
    private static RawEntry Folder(string name) => new(name, true, 0, DateTime.UtcNow, FileAttributes.Directory);

    [Fact]
    public void Defaults_SkipBinAndObj_OnNetworkLocationsOnly()
    {
        var settings = new AppSettings();
        Assert.Equal(["bin", "obj"], settings.ExcludedNetworkFolderNames);

        var network = settings.CreateExcludeRules(RootKind.Network);
        var local = settings.CreateExcludeRules(RootKind.Local);
        var removable = settings.CreateExcludeRules(RootKind.Removable);

        Assert.True(network.IsExcluded(@"L:\CMS\Project", Folder("bin")));
        Assert.True(network.IsExcluded(@"L:\CMS\Project", Folder("OBJ")));
        Assert.True(network.IsExcluded(@"L:\CMS\Project", Folder("node_modules")));
        Assert.False(network.IsExcluded(@"L:\CMS\Project", Folder("binaries")));
        Assert.False(local.IsExcluded(@"C:\Code\Project", Folder("bin")));
        Assert.False(removable.IsExcluded(@"E:\Project", Folder("obj")));
        Assert.NotEqual(network.Fingerprint, local.Fingerprint);
    }

    [Fact]
    public void ExistingSettingsFileWithoutTheList_GetsTheDefault_AnEmptyListStaysEmpty()
    {
        var old = JsonSerializer.Deserialize<AppSettings>("""{ "schemaVersion": 1, "excludedFolderNames": [ ".git" ] }""")!;
        Assert.Equal(["bin", "obj"], old.Sanitized().ExcludedNetworkFolderNames);

        var cleared = JsonSerializer.Deserialize<AppSettings>("""{ "schemaVersion": 1, "excludedNetworkFolderNames": [] }""")!;
        Assert.Empty(cleared.Sanitized().ExcludedNetworkFolderNames);
        Assert.False(cleared.CreateExcludeRules(RootKind.Network).IsExcluded(@"L:\x", Folder("bin")));
    }

    [Fact]
    public void ResetToDefaults_RestoresTheNetworkList()
    {
        var changed = new AppSettings { ExcludedNetworkFolderNames = ["Archiv"] };
        Assert.Equal(["bin", "obj"], changed.WithDefaultExclusions().ExcludedNetworkFolderNames);
    }
}
