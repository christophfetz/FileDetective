using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Exclusions of PLAN.md 7.3.</summary>
public class ExcludeRulesTests
{
    private const FileAttributes Dir = FileAttributes.Directory;
    private const FileAttributes File = FileAttributes.Archive;

    private static string Expand(string s) => s
        .Replace("%WINDIR%", @"C:\Windows", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles%", @"C:\Program Files", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramFiles(x86)%", @"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase)
        .Replace("%ProgramData%", @"C:\ProgramData", StringComparison.OrdinalIgnoreCase)
        .Replace("%SHARE%", @"\\server\share", StringComparison.OrdinalIgnoreCase);

    private static ExcludeRules Defaults() => new(
        ExcludeRules.DefaultFolderNames, ExcludeRules.DefaultPaths, ExcludeRules.DefaultFileNames, true, Expand);

    [Theory]
    [InlineData(@"C:\Users\anna\Projekt", "node_modules")]
    [InlineData(@"C:\Users\anna\Projekt", ".GIT")]
    [InlineData(@"\\server\share\x", ".svn")]
    [InlineData(@"D:\", "$Recycle.Bin")]
    [InlineData(@"D:\", "System Volume Information")]
    [InlineData(@"C:\code", "__pycache__")]
    [InlineData(@"C:\code", ".vs")]
    [InlineData(@"C:\code", ".idea")]
    [InlineData(@"C:\code", ".hg")]
    public void FolderNames_AreExcludedEverywhere(string parent, string name)
        => Assert.True(Defaults().IsExcluded(parent, name, true, Dir));

    [Fact]
    public void FolderNames_DoNotApplyToFilesOrSimilarNames()
    {
        var rules = Defaults();
        Assert.False(rules.IsExcluded(@"C:\code", "node_modules", false, File));
        Assert.False(rules.IsExcluded(@"C:\code", "node_modules2", true, Dir));
        Assert.False(rules.IsExcluded(@"C:\code", "git", true, Dir));
    }

    [Theory]
    [InlineData(@"C:\", "Windows", true)]
    [InlineData(@"c:\", "WINDOWS", true)]
    [InlineData(@"C:\", "Program Files", true)]
    [InlineData(@"C:\", "Program Files (x86)", true)]
    [InlineData(@"C:\", "ProgramData", true)]
    [InlineData(@"C:\Users\anna", "AppData", true)]
    [InlineData(@"C:\Users\Bob Smith", "appdata", true)]
    [InlineData(@"C:\Users\anna", "AppDataX", false)]
    [InlineData(@"C:\Users\anna\Documents", "AppData", false)] // other depth
    [InlineData(@"C:\Users", "AppData", false)]
    [InlineData(@"D:\", "Windows", false)]
    [InlineData(@"C:\Windows", "System32", false)] // below an excluded folder: never reached, no rule needed
    public void Paths_WithVariablesAndStarPerPart(string parent, string name, bool excluded)
        => Assert.Equal(excluded, Defaults().IsExcluded(parent, name, true, Dir));

    [Fact]
    public void Paths_StarInsidePart_UncAndFiles()
    {
        var rules = new ExcludeRules([], [@"%SHARE%\Archiv*\alt", @"D:\data\*.bak", @"E:/forward/slashes"], [], true, Expand);
        Assert.True(rules.IsExcluded(@"\\server\share\Archiv 2020", "alt", true, Dir));
        Assert.True(rules.IsExcluded(@"\\server\share\Archiv", "ALT", true, Dir));
        Assert.False(rules.IsExcluded(@"\\server\share\Projekte", "alt", true, Dir));
        Assert.False(rules.IsExcluded(@"\\server\other\Archiv", "alt", true, Dir));
        Assert.True(rules.IsExcluded(@"D:\data", "x.bak", false, File)); // full path rules also hit files
        Assert.True(rules.IsExcluded(@"E:\forward", "slashes", true, Dir));
    }

    [Fact]
    public void Paths_UnknownVariable_RuleIsIgnored()
    {
        var rules = new ExcludeRules([], ["%NO_SUCH_VARIABLE_FD%", @"%NO_SUCH_VARIABLE_FD%\x"], [], true, s => s);
        Assert.False(rules.IsExcluded(@"C:\", "x", true, Dir));
        Assert.False(rules.IsExcluded(@"C:\NO_SUCH_VARIABLE_FD", "x", true, Dir));
    }

    [Fact]
    public void Paths_RealEnvironmentVariablesAreExpanded()
    {
        string windir = Environment.GetEnvironmentVariable("WINDIR")!;
        var rules = ExcludeRules.CreateDefault();
        Assert.True(rules.IsExcluded(Path.GetDirectoryName(windir)!, Path.GetFileName(windir), true, Dir));
    }

    [Theory]
    [InlineData("~$Angebot.docx", true)]
    [InlineData("Thumbs.db", true)]
    [InlineData("THUMBS.DB", true)]
    [InlineData("desktop.ini", true)]
    [InlineData("x.tmp", true)]
    [InlineData("x.TMP", true)]
    [InlineData("x.tmp.txt", false)]
    [InlineData("Angebot~$.docx", false)]
    [InlineData("tmp", false)]
    public void FileNames_WithStar(string name, bool excluded)
        => Assert.Equal(excluded, Defaults().IsExcluded(@"C:\Users\anna\Docs", name, false, File));

    [Fact]
    public void FileNamePatterns_DoNotApplyToFolders()
        => Assert.False(Defaults().IsExcluded(@"C:\x", "backup.tmp", true, Dir));

    [Fact]
    public void SystemAttribute_IsExcluded_HiddenIsKept()
    {
        var rules = Defaults();
        Assert.True(rules.IsExcluded(@"C:\x", "a.txt", false, File | FileAttributes.System));
        Assert.True(rules.IsExcluded(@"C:\x", "folder", true, Dir | FileAttributes.System | FileAttributes.Hidden));
        Assert.False(rules.IsExcluded(@"C:\x", ".hidden", false, File | FileAttributes.Hidden));
        Assert.False(rules.IsExcluded(@"C:\x", "hiddendir", true, Dir | FileAttributes.Hidden));
        Assert.False(ExcludeRules.None.IsExcluded(@"C:\x", "a.txt", false, File | FileAttributes.System));
    }

    [Fact]
    public void ListsArePassedIn()
    {
        var rules = new ExcludeRules(["bin", "obj"], [], ["*.log"]);
        Assert.True(rules.IsExcluded(@"C:\x", "BIN", true, Dir));
        Assert.True(rules.IsExcluded(@"C:\x", "a.log", false, File));
        Assert.False(rules.IsExcluded(@"C:\x", "node_modules", true, Dir));
        Assert.False(rules.IsExcluded(@"C:\x", "a.tmp", false, File));
    }
}
