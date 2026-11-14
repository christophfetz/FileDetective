using System.Text.Json;
using FileDetective.Core;

namespace FileDetective.Tests;

public class SettingsServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string FilePath => Path.Combine(_dir.Path, "settings.json");

    private SettingsService NewService() => new(FilePath);

    [Fact]
    public void MissingFile_GivesDefaults_AndWritesNothing()
    {
        var service = NewService();
        Assert.Equal(SettingsLoadResult.Missing, service.Load());

        var s = service.Current;
        Assert.Equal(1, s.SchemaVersion);
        Assert.Equal("Alt+Space", s.Hotkey);
        Assert.Equal(HotkeyGesture.Default, s.HotkeyValue);
        Assert.Equal(AppSettings.LanguageSystem, s.Language);
        Assert.True(s.CheckForUpdates);
        Assert.Equal(NetworkPoliteness.Normal, s.NetworkPolitenessLevel);
        Assert.False(s.FirstRunCompleted);
        Assert.Equal(ExcludeRules.DefaultFolderNames, s.ExcludedFolderNames);
        Assert.Equal(ExcludeRules.DefaultPaths, s.ExcludedPaths);
        Assert.Equal(ExcludeRules.DefaultFileNames, s.ExcludedFileNames);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void RoundTrip_KeepsEveryField()
    {
        var service = NewService();
        service.Load();
        service.Save(new AppSettings
        {
            Hotkey = "Ctrl+Alt+K",
            Language = AppSettings.LanguageGerman,
            CheckForUpdates = false,
            Politeness = AppSettings.PolitenessVeryGentle,
            ExcludedFolderNames = ["obj", "bin"],
            ExcludedPaths = [@"D:\Temp\*", @"%USERPROFILE%\Downloads"],
            ExcludedFileNames = ["*.bak"],
            FirstRunCompleted = true,
        });

        var again = NewService();
        Assert.Equal(SettingsLoadResult.Loaded, again.Load());
        var s = again.Current;
        Assert.Equal("Ctrl+Alt+K", s.Hotkey);
        Assert.Equal(AppLanguage.German, s.LanguageChoice);
        Assert.False(s.CheckForUpdates);
        Assert.Equal(NetworkPoliteness.VeryGentle, s.NetworkPolitenessLevel);
        Assert.Equal(["obj", "bin"], s.ExcludedFolderNames);
        Assert.Equal([@"D:\Temp\*", @"%USERPROFILE%\Downloads"], s.ExcludedPaths);
        Assert.Equal(["*.bak"], s.ExcludedFileNames);
        Assert.True(s.FirstRunCompleted);
    }

    [Fact]
    public void FileUsesTheFieldNamesOfThePlan()
    {
        var service = NewService();
        service.Save(new AppSettings { FirstRunCompleted = true });
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        string[] names = [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
        foreach (string expected in new[]
        {
            "schemaVersion", "hotkey", "language", "checkForUpdates", "networkPoliteness",
            "excludedFolderNames", "excludedPaths", "excludedFileNames", "firstRunCompleted",
        })
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public void ClearedList_StaysEmpty_MissingListGetsDefaults()
    {
        File.WriteAllText(FilePath, """{ "schemaVersion": 1, "excludedFolderNames": [], "excludedFileNames": null }""");
        var service = NewService();
        Assert.Equal(SettingsLoadResult.Loaded, service.Load());
        Assert.Empty(service.Current.ExcludedFolderNames);
        Assert.Equal(ExcludeRules.DefaultFileNames, service.Current.ExcludedFileNames);
        Assert.Equal(ExcludeRules.DefaultPaths, service.Current.ExcludedPaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ this is not json")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    [InlineData("""{ "schemaVersion": 1, "checkForUpdates": "yes please" }""")]
    [InlineData("""{ "schemaVersion": 0 }""")]
    public void BrokenFile_IsRenamed_AndDefaultsApply(string content)
    {
        File.WriteAllText(FilePath, content);
        var service = NewService();

        Assert.Equal(SettingsLoadResult.CorruptRenamed, service.Load());
        Assert.False(File.Exists(FilePath));
        Assert.NotNull(service.BackupPath);
        Assert.True(File.Exists(service.BackupPath));
        Assert.Equal(content, File.ReadAllText(service.BackupPath));
        Assert.Equal("Alt+Space", service.Current.Hotkey);
        Assert.False(service.Current.FirstRunCompleted);

        // The program goes on: the next save creates a fresh file next to the backup.
        service.Save(service.Current with { FirstRunCompleted = true });
        Assert.True(File.Exists(FilePath));
        Assert.True(File.Exists(service.BackupPath));
    }

    [Fact]
    public void TwoBrokenFilesInTheSameSecond_DoNotOverwriteEachOther()
    {
        var fixedClock = new FixedClock(new DateTimeOffset(2026, 11, 13, 8, 0, 0, TimeSpan.Zero));
        File.WriteAllText(FilePath, "{ one");
        var first = new SettingsService(FilePath, fixedClock);
        first.Load();
        File.WriteAllText(FilePath, "{ two");
        var second = new SettingsService(FilePath, fixedClock);
        second.Load();

        Assert.NotEqual(first.BackupPath, second.BackupPath);
        Assert.Equal("{ one", File.ReadAllText(first.BackupPath!));
        Assert.Equal("{ two", File.ReadAllText(second.BackupPath!));
    }

    [Fact]
    public void UnknownFields_AreIgnored_AndKeptOnSave()
    {
        File.WriteAllText(
            FilePath,
            """
            {
              // comments and trailing commas are tolerated
              "schemaVersion": 1,
              "hotkey": "Ctrl+Shift+F",
              "futureOption": { "a": [1, 2, 3] },
              "anotherOne": "x",
            }
            """);
        var service = NewService();
        Assert.Equal(SettingsLoadResult.Loaded, service.Load());
        Assert.Equal("Ctrl+Shift+F", service.Current.Hotkey);
        Assert.Equal(AppSettings.LanguageSystem, service.Current.Language); // missing field: default

        service.Update(s => s with { FirstRunCompleted = true });
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal("x", doc.RootElement.GetProperty("anotherOne").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("futureOption").GetProperty("a").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("firstRunCompleted").GetBoolean());
    }

    [Fact]
    public void NewerSchemaVersion_IsReadAndNotDowngraded()
    {
        File.WriteAllText(FilePath, """{ "schemaVersion": 7, "language": "en" }""");
        var service = NewService();
        Assert.Equal(SettingsLoadResult.Loaded, service.Load());
        Assert.Equal(AppLanguage.English, service.Current.LanguageChoice);

        service.Update(s => s with { CheckForUpdates = false });
        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(7, doc.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void InvalidValues_BecomeDefaults()
    {
        File.WriteAllText(
            FilePath,
            """{ "hotkey": "Q", "language": "klingon", "networkPoliteness": "extreme", "excludedPaths": ["", "  ", "C:\\x"] }""");
        var service = NewService();
        Assert.Equal(SettingsLoadResult.Loaded, service.Load());
        var s = service.Current;
        Assert.Equal("Alt+Space", s.Hotkey);
        Assert.Equal(AppSettings.LanguageSystem, s.Language);
        Assert.Equal(NetworkPoliteness.Normal, s.NetworkPolitenessLevel);
        Assert.Equal([@"C:\x"], s.ExcludedPaths);
    }

    [Fact]
    public void Save_IsAtomic_NoTemporaryFileRemains_AndOverwrites()
    {
        var service = NewService();
        service.Save(new AppSettings { Language = AppSettings.LanguageEnglish });
        service.Save(new AppSettings { Language = AppSettings.LanguageGerman });

        Assert.Equal([FilePath], Directory.GetFiles(_dir.Path));
        var again = NewService();
        again.Load();
        Assert.Equal(AppLanguage.German, again.Current.LanguageChoice);
    }

    [Fact]
    public void Save_CreatesTheFolder()
    {
        string nested = Path.Combine(_dir.Path, "a", "b", "settings.json");
        new SettingsService(nested).Save(new AppSettings());
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Update_RaisesChanged_AndFailureLeavesCurrentUnchanged()
    {
        var service = NewService();
        AppSettings? seen = null;
        service.Changed += s => seen = s;
        service.Update(s => s with { CheckForUpdates = false });
        Assert.NotNull(seen);
        Assert.False(seen!.CheckForUpdates);

        // A folder in the place of the file makes writing fail.
        string blocked = Path.Combine(_dir.Path, "blocked.json");
        Directory.CreateDirectory(blocked);
        var failing = new SettingsService(blocked);
        Assert.ThrowsAny<Exception>(() => failing.Update(s => s with { CheckForUpdates = false }));
        Assert.True(failing.Current.CheckForUpdates);
    }

    [Fact]
    public void ExcludeRules_FromSettings_ExpandPlaceholdersAndWildcards()
    {
        string name = "FD_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, @"D:\Base");
        try
        {
            var settings = new AppSettings
            {
                ExcludedFolderNames = ["cache"],
                ExcludedPaths = [$@"%{name}%\*\private"],
                ExcludedFileNames = ["*.bak"],
            };
            var rules = settings.CreateExcludeRules();
            Assert.True(rules.IsExcluded(@"D:\Base\anna", "private", true, FileAttributes.Directory));
            Assert.False(rules.IsExcluded(@"D:\Other\anna", "private", true, FileAttributes.Directory));
            Assert.True(rules.IsExcluded(@"C:\x", "CACHE", true, FileAttributes.Directory));
            Assert.True(rules.IsExcluded(@"C:\x", "a.bak", false, FileAttributes.Normal));
            Assert.False(rules.IsExcluded(@"C:\x", "node_modules", true, FileAttributes.Directory)); // defaults were replaced
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void DefaultSettings_GiveTheDefaultRules_AndResetRestoresThem()
    {
        var custom = new AppSettings { ExcludedFolderNames = ["x"], ExcludedPaths = [], ExcludedFileNames = [] };
        var reset = custom.WithDefaultExclusions();
        Assert.Equal(ExcludeRules.DefaultFolderNames, reset.ExcludedFolderNames);
        Assert.Equal(ExcludeRules.DefaultPaths, reset.ExcludedPaths);
        Assert.Equal(ExcludeRules.DefaultFileNames, reset.ExcludedFileNames);
        Assert.True(new AppSettings().CreateExcludeRules().IsExcluded(@"C:\p", "node_modules", true, FileAttributes.Directory));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
