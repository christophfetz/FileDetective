using System.ComponentModel;
using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>
/// After stage 10 (PLAN.md 2.6, 11.1 "Gesperrte Ordner und Admin-Analyse"): the list of locked folders, the command line,
/// what the storage process sets up, the button "Analyze as administrator…" and the read-only settings of that process.
/// </summary>
public class StorageAdminTests : IDisposable
{
    private const string Drive = @"R:\";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ---------------------------------------------------------------- locked folders

    private sealed class FixedSpace(long used) : IDriveSpaceSource
    {
        public DriveSpace? GetSpace(string driveRoot) => new DriveSpace(1_000_000, 1_000_000 - used);
    }

    private static SpaceSnapshot Scan(FakeFileSystem fs)
        => new LocalSpaceScanner(fs, new FakeLinkDetector(), new FixedSpace(10_000)).Scan(Drive, RootKind.Local);

    [Fact]
    public void LockedFolders_AreTheFoldersThatCouldNotBeListed_SortedByPath()
    {
        var fs = new FakeFileSystem(Drive);
        fs.Dir("Program Files").Dir(@"Program Files\WindowsApps").File(@"Program Files\WindowsApps\app.dll", 500)
            .Dir("System Volume Information").Dir("Recovery").Dir("Users").Dir(@"Users\anna").File(@"Users\anna\a.txt", 100)
            .Dir(@"Users\bob");
        fs.Fail(@"Program Files\WindowsApps", new UnauthorizedAccessException("denied"));
        fs.Fail("System Volume Information", new UnauthorizedAccessException("denied"));
        fs.Fail("Recovery", new UnauthorizedAccessException("denied"));
        fs.Fail(@"Users\bob", new IOException("in use"));

        var snap = Scan(fs);
        var locked = snap.GetLockedFolders();

        Assert.Equal(
            [@"R:\Program Files\WindowsApps", @"R:\Recovery", @"R:\System Volume Information", @"R:\Users\bob"],
            locked.Paths);
        Assert.Equal(0, locked.OmittedCount);
        Assert.Equal(snap.FailedDirectories, locked.TotalCount);
    }

    [Fact]
    public void LockedFolders_WithoutFailures_IsEmpty()
    {
        var fs = new FakeFileSystem(Drive);
        fs.Dir("Docs").File(@"Docs\a.txt", 100);

        var locked = Scan(fs).GetLockedFolders();

        Assert.Empty(locked.Paths);
        Assert.Equal(0, locked.OmittedCount);
    }

    [Fact]
    public void LockedFolders_AreCutAt200_WithTheNumberOfTheRest()
    {
        var fs = new FakeFileSystem(Drive);
        for (int i = 0; i < 250; i++)
        {
            string name = $"Locked{i:D3}";
            fs.Dir(name);
            fs.Fail(name, new UnauthorizedAccessException("denied"));
        }

        var locked = Scan(fs).GetLockedFolders();

        Assert.Equal(LockedFolderList.DefaultLimit, locked.Paths.Count);
        Assert.Equal(200, locked.Paths.Count);
        Assert.Equal(50, locked.OmittedCount);
        Assert.Equal(250, locked.TotalCount);
        Assert.Equal(@"R:\Locked000", locked.Paths[0]);
        Assert.Equal(@"R:\Locked199", locked.Paths[^1]);
    }

    [Fact]
    public void LockedFolderList_SortsIgnoringCase_AndKeepsAllUpToTheLimit()
    {
        var list = LockedFolderList.Create([@"C:\b", @"C:\A", @"C:\a\x", @"C:\B\y"], limit: 3);

        Assert.Equal([@"C:\A", @"C:\a\x", @"C:\b"], list.Paths);
        Assert.Equal(1, list.OmittedCount);
        Assert.Equal(4, LockedFolderList.Create([@"C:\1", @"C:\2", @"C:\3", @"C:\4"], limit: 4).Paths.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => LockedFolderList.Create([], limit: 0));
    }

    [Fact]
    public void LockedFolders_MovedToTheRecycleBin_AreNoLongerListed()
    {
        var fs = new FakeFileSystem(Drive);
        fs.Dir("Old").Dir(@"Old\Locked").Dir("Kept");
        fs.Fail(@"Old\Locked", new UnauthorizedAccessException("denied"));
        fs.Fail("Kept", new UnauthorizedAccessException("denied"));
        var snap = Scan(fs);

        snap.RemoveFolder(snap.Root.Children.Single(c => c.Name == "Old"));

        Assert.Equal([@"R:\Kept"], snap.GetLockedFolders().Paths);
    }

    // ---------------------------------------------------------------- command line and start-up plan

    [Theory]
    [InlineData(new string[0], StartupMode.Normal, null)]
    [InlineData(new[] { "--autostart" }, StartupMode.Autostart, null)]
    [InlineData(new[] { "--AUTOSTART" }, StartupMode.Autostart, null)]
    [InlineData(new[] { "--restart-after", "4711" }, StartupMode.Autostart, 4711)]
    [InlineData(new[] { "--restart-after" }, StartupMode.Autostart, null)]
    [InlineData(new[] { "--restart-after", "abc" }, StartupMode.Autostart, null)]
    [InlineData(new[] { "--storage-admin" }, StartupMode.StorageAdmin, null)]
    [InlineData(new[] { "--Storage-Admin" }, StartupMode.StorageAdmin, null)]
    [InlineData(new[] { "--autostart", "--storage-admin" }, StartupMode.StorageAdmin, null)]
    [InlineData(new[] { "--restart-after", "12", "--storage-admin" }, StartupMode.StorageAdmin, null)]
    [InlineData(new[] { "--something-else" }, StartupMode.Normal, null)]
    public void StartupOptions_Parse(string[] args, StartupMode mode, int? restartAfter)
    {
        var options = StartupOptions.Parse(args);

        Assert.Equal(mode, options.Mode);
        Assert.Equal(restartAfter, options.RestartAfterProcessId);
    }

    [Fact]
    public void StorageProcess_SetsUpOnlyTheStorageWindow_AndWritesNothing()
    {
        var plan = StartupPlan.For(StartupMode.StorageAdmin);

        Assert.True(plan.OnlyStorageWindow);
        Assert.True(plan.HoldRunningMarker); // the installer must still see it
        Assert.False(plan.SingleInstance); // runs beside the normal instance
        Assert.False(plan.WriteLog);
        Assert.False(plan.WritableSettings);
        Assert.False(plan.OpenDirectory);
        Assert.False(plan.TrayIcon);
        Assert.False(plan.Hotkey);
        Assert.False(plan.UpdateCheck);
        Assert.False(plan.FirstRunSetup);
        Assert.False(plan.ShowShares);
    }

    [Theory]
    [InlineData(StartupMode.Normal, true)]
    [InlineData(StartupMode.Autostart, false)]
    public void NormalProcess_SetsUpEverything(StartupMode mode, bool firstRun)
    {
        var plan = StartupPlan.For(mode);

        Assert.False(plan.OnlyStorageWindow);
        Assert.True(plan.SingleInstance);
        Assert.True(plan.HoldRunningMarker);
        Assert.True(plan.WriteLog);
        Assert.True(plan.WritableSettings);
        Assert.True(plan.OpenDirectory);
        Assert.True(plan.TrayIcon);
        Assert.True(plan.Hotkey);
        Assert.True(plan.UpdateCheck);
        Assert.True(plan.ShowShares);
        Assert.Equal(firstRun, plan.FirstRunSetup);
    }

    // ---------------------------------------------------------------- "Analyze as administrator…"

    private sealed class FakeElevation(bool elevated) : IElevationProbe
    {
        public bool IsElevated { get; } = elevated;
    }

    private sealed class FakeStarter(Exception? failure = null) : IElevatedStarter
    {
        public List<(string Exe, string Arguments)> Calls { get; } = [];

        public void Start(string exePath, string arguments)
        {
            Calls.Add((exePath, arguments));
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    [Theory]
    [InlineData(StartupMode.Normal, false, true)]
    [InlineData(StartupMode.Autostart, false, true)]
    [InlineData(StartupMode.Normal, true, false)] // already elevated: the normal window reads everything
    [InlineData(StartupMode.StorageAdmin, false, false)]
    [InlineData(StartupMode.StorageAdmin, true, false)]
    public void AdminButton_IsOnlyOfferedWhenNotElevated_AndNotInTheStorageProcess(StartupMode mode, bool elevated, bool offered)
        => Assert.Equal(offered, StorageAdminLauncher.CanOffer(mode, new FakeElevation(elevated)));

    [Fact]
    public void Launch_StartsTheSameProgram_WithTheStorageSwitch()
    {
        var starter = new FakeStarter();

        var result = StorageAdminLauncher.Launch(starter, @"C:\Apps\FileDetective.exe");

        Assert.Equal(AdminLaunchStatus.Started, result.Status);
        Assert.Null(result.Error);
        Assert.Equal([(@"C:\Apps\FileDetective.exe", "--storage-admin")], starter.Calls);
        Assert.Equal(StartupMode.StorageAdmin, StartupOptions.Parse([starter.Calls[0].Arguments]).Mode);
    }

    [Fact]
    public void Launch_DeclinedPrompt_IsReportedAsDeclined_WithoutError()
    {
        var starter = new FakeStarter(new Win32Exception(1223, "The operation was canceled by the user."));

        var result = StorageAdminLauncher.Launch(starter, @"C:\Apps\FileDetective.exe");

        Assert.Equal(AdminLaunchStatus.Declined, result.Status);
        Assert.Null(result.Error);
        Assert.Single(starter.Calls); // tried once, never again
    }

    [Theory]
    [InlineData(2)] // file not found
    [InlineData(5)] // access denied
    [InlineData(1155)] // no application associated
    public void Launch_OtherWindowsErrors_AreFailures_WithTheError(int code)
    {
        var failure = new Win32Exception(code);

        var result = StorageAdminLauncher.Launch(new FakeStarter(failure), @"C:\Apps\FileDetective.exe");

        Assert.Equal(AdminLaunchStatus.Failed, result.Status);
        Assert.Same(failure, result.Error);
    }

    [Fact]
    public void Launch_OtherExceptions_AreFailures_AndNeverThrow()
    {
        var result = StorageAdminLauncher.Launch(new FakeStarter(new InvalidOperationException("no shell")), @"C:\x.exe");

        Assert.Equal(AdminLaunchStatus.Failed, result.Status);
        Assert.IsType<InvalidOperationException>(result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Launch_WithoutProgramPath_FailsWithoutStartingAnything(string? exe)
    {
        var starter = new FakeStarter();

        var result = StorageAdminLauncher.Launch(starter, exe);

        Assert.Equal(AdminLaunchStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        Assert.Empty(starter.Calls);
    }

    // ---------------------------------------------------------------- read-only settings: nothing in the data folder

    private string SettingsPath => Path.Combine(_dir.Path, "settings.json");

    private string[] DataFolderContents()
        => [.. Directory.GetFileSystemEntries(_dir.Path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)];

    [Fact]
    public void ReadOnlySettings_ReadTheLanguage_AndWriteNothing()
    {
        new SettingsService(SettingsPath).Save(new AppSettings { Language = AppSettings.LanguageGerman });
        var before = DataFolderContents();
        var bytes = File.ReadAllBytes(SettingsPath);
        var time = File.GetLastWriteTimeUtc(SettingsPath);

        var service = new SettingsService(SettingsPath, readOnly: true);
        Assert.Equal(SettingsLoadResult.Loaded, service.Load());

        Assert.Equal(AppLanguage.German, service.Current.LanguageChoice);
        Assert.True(service.IsReadOnly);
        Assert.Equal(before, DataFolderContents());
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
        Assert.Equal(time, File.GetLastWriteTimeUtc(SettingsPath));
    }

    [Fact]
    public void ReadOnlySettings_DamagedFile_IsLeftInPlace()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        var before = DataFolderContents();

        var service = new SettingsService(SettingsPath, readOnly: true);
        Assert.Equal(SettingsLoadResult.CorruptIgnored, service.Load());

        Assert.Null(service.BackupPath);
        Assert.Equal(before, DataFolderContents());
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
        Assert.Equal(AppSettings.LanguageSystem, service.Current.Language);
    }

    [Fact]
    public void ReadOnlySettings_MissingFile_CreatesNothing_AndRefusesToSave()
    {
        string folder = Path.Combine(_dir.Path, "never-created");
        var service = new SettingsService(Path.Combine(folder, "settings.json"), readOnly: true);

        Assert.Equal(SettingsLoadResult.Missing, service.Load());
        Assert.Throws<InvalidOperationException>(() => service.Save(new AppSettings()));
        Assert.Throws<InvalidOperationException>(() => service.Update(s => s with { FirstRunCompleted = true }));

        Assert.False(Directory.Exists(folder));
        Assert.False(service.Current.FirstRunCompleted);
    }

    [Fact]
    public void SharedLog_WithoutInitialize_WritesNothing()
    {
        // The storage process never calls FileLog.Initialize; a log without a path writes no file.
        var log = new FileLog(null);
        log.Info("x");
        log.Error("y", new InvalidOperationException("z"));

        Assert.Null(log.Path);
        Assert.Empty(DataFolderContents());
    }
}
