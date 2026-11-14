using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>The Start menu folders are always recorded (PLAN.md 7.3, "Startmenü").</summary>
public class StartMenuTests
{
    private const FileAttributes Dir = FileAttributes.Directory;
    private const FileAttributes File = FileAttributes.Archive;
    private const FileAttributes SystemDir = FileAttributes.Directory | FileAttributes.System;

    private static string Expand(string s) => s
        .Replace("%APPDATA%", @"C:\Users\anna\AppData\Roaming", StringComparison.OrdinalIgnoreCase)
        .Replace("%WINDIR%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles(x86)%", @"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramData%", @"C:\ProgramData", StringComparison.OrdinalIgnoreCase);

    private static ExcludeRules Defaults() => new(
        ExcludeRules.DefaultFolderNames, ExcludeRules.DefaultPaths, ExcludeRules.DefaultFileNames, true, Expand,
        ExcludeRules.AlwaysIncludedPaths);

    [Theory]
    [InlineData(@"C:\", "ProgramData", Dir)]
    [InlineData(@"C:\ProgramData", "Microsoft", SystemDir)] // carries the System attribute in real life
    [InlineData(@"C:\ProgramData\Microsoft", "Windows", Dir)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows", "Start Menu", Dir)]
    [InlineData(@"C:\Users\anna", "AppData", Dir)]
    [InlineData(@"C:\Users\anna\AppData", "Roaming", Dir)]
    [InlineData(@"C:\Users\anna\AppData\Roaming\Microsoft\Windows", "start menu", Dir)] // case does not matter
    public void TheWayToTheStartMenu_IsKept(string parent, string name, FileAttributes attributes)
        => Assert.False(Defaults().IsExcluded(parent, name, true, attributes));

    [Theory]
    [InlineData(@"C:\ProgramData", "Package Cache", Dir)]
    [InlineData(@"C:\ProgramData", "notes.txt", File)]
    [InlineData(@"C:\ProgramData\Microsoft", "Crypto", Dir)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows", "Caches", Dir)]
    [InlineData(@"C:\Users\anna\AppData", "Local", Dir)]
    [InlineData(@"C:\Users\anna\AppData\Roaming", "Code", Dir)]
    [InlineData(@"C:\Users\anna\AppData\Roaming\Microsoft\Windows", "Recent", Dir)]
    [InlineData(@"C:\Users\anna\AppData\Roaming\Microsoft\Windows", "Start Menu.lnk", File)] // a file is not the folder
    public void EverythingBesideTheWay_BelowAnExcludedFolder_StaysExcluded(string parent, string name, FileAttributes attributes)
        => Assert.True(Defaults().IsExcluded(parent, name, attributes.HasFlag(FileAttributes.Directory), attributes));

    [Fact]
    public void AboveTheExcludedFolder_TheNormalRulesApply()
    {
        var rules = Defaults();
        Assert.False(rules.IsExcluded(@"C:\", "Daten", true, Dir));
        Assert.False(rules.IsExcluded(@"C:\Users\anna", "Documents", true, Dir));
        Assert.True(rules.IsExcluded(@"C:\Users\anna", "node_modules", true, Dir));
        Assert.True(rules.IsExcluded(@"C:\", "Windows", true, Dir));
        Assert.True(rules.IsExcluded(@"C:\Users\anna", "System", true, SystemDir));
    }

    [Fact]
    public void InsideTheStartMenu_TheNormalRulesApply()
    {
        var rules = Defaults();
        string programs = @"C:\Users\anna\AppData\Roaming\Microsoft\Windows\Start Menu\Programs";
        Assert.False(rules.IsExcluded(@"C:\Users\anna\AppData\Roaming\Microsoft\Windows\Start Menu", "Programs", true, Dir | FileAttributes.ReadOnly));
        Assert.False(rules.IsExcluded(programs, "Claude.lnk", false, File));
        Assert.False(rules.IsExcluded(programs, "Accessories", true, Dir));
        Assert.True(rules.IsExcluded(programs, "desktop.ini", false, File | FileAttributes.System | FileAttributes.Hidden));
        Assert.True(rules.IsExcluded(@"C:\Users\anna\AppData\Roaming\Microsoft\Windows\Start Menu", "Programme", true, SystemDir | FileAttributes.ReparsePoint));
    }

    [Fact]
    public void WithoutIncludedPaths_TheStartMenuIsExcluded()
    {
        var rules = new ExcludeRules(ExcludeRules.DefaultFolderNames, ExcludeRules.DefaultPaths, ExcludeRules.DefaultFileNames, true, Expand);
        Assert.True(rules.IsExcluded(@"C:\", "ProgramData", true, Dir));
        Assert.True(rules.IsExcluded(@"C:\Users\anna", "AppData", true, Dir));
    }

    [Fact]
    public void TrailingSeparatorOfTheParent_DoesNotMatter()
    {
        var rules = Defaults();
        Assert.False(rules.IsExcluded(@"C:\Users\anna\", "AppData", true, Dir));
        Assert.True(rules.IsExcluded(@"C:\Users\anna\AppData\", "Local", true, Dir));
    }

    [Fact]
    public void SettingsRules_IncludeTheStartMenu()
    {
        var rules = new AppSettings().CreateExcludeRules();
        string startMenu = Environment.ExpandEnvironmentVariables(@"%ProgramData%\Microsoft\Windows");
        Assert.False(rules.IsExcluded(startMenu, "Start Menu", true, Dir));
        Assert.False(ExcludeRules.CreateDefault().IsExcluded(startMenu, "Start Menu", true, Dir));
    }

    // ---------------------------------------------------------------- locations

    private static RootInfo Root(byte id, string path) =>
        new(id, path, null, RootKind.Local, true, null, null, ScanStatus.Never, 0, 0, id);

    [Fact]
    public void Missing_UserFolderOnly_AddsTheCommonStartMenu()
    {
        var missing = StartMenuLocations.Missing([Root(1, @"C:\Users\anna")], _ => true, Expand);
        Assert.Equal([@"C:\ProgramData\Microsoft\Windows\Start Menu"], missing);
    }

    [Fact]
    public void Missing_DriveLocation_CoversBoth()
        => Assert.Empty(StartMenuLocations.Missing([Root(1, @"C:\")], _ => true, Expand));

    [Fact]
    public void Missing_NoLocations_AddsBoth()
    {
        var missing = StartMenuLocations.Missing([], _ => true, Expand);
        Assert.Equal(
            [@"C:\Users\anna\AppData\Roaming\Microsoft\Windows\Start Menu", @"C:\ProgramData\Microsoft\Windows\Start Menu"],
            missing);
    }

    [Fact]
    public void Missing_AlreadyALocation_IsNotAddedAgain()
    {
        var roots = new[] { Root(1, @"C:\Users\anna"), Root(2, @"c:\programdata\microsoft\windows\start menu") };
        Assert.Empty(StartMenuLocations.Missing(roots, _ => true, Expand));
    }

    [Fact]
    public void Missing_FolderThatDoesNotExist_IsSkipped()
    {
        var missing = StartMenuLocations.Missing([], p => p.StartsWith(@"C:\ProgramData", StringComparison.Ordinal), Expand);
        Assert.Equal([@"C:\ProgramData\Microsoft\Windows\Start Menu"], missing);
    }

    [Fact]
    public void Missing_UnsetVariable_IsSkipped()
        => Assert.Empty(StartMenuLocations.Missing([], _ => true, s => s));

    [Fact]
    public void Setting_StartMenuChecked_DefaultsToFalseAndRoundTrips()
    {
        Assert.False(new AppSettings().StartMenuChecked);
        string json = System.Text.Json.JsonSerializer.Serialize(new AppSettings { StartMenuChecked = true });
        Assert.Contains("\"startMenuChecked\":true", json, StringComparison.Ordinal);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!.StartMenuChecked);
    }
}
