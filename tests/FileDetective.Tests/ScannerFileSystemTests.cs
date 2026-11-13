using FileDetective.Core;
using Xunit.Abstractions;

namespace FileDetective.Tests;

/// <summary>Scanner and <see cref="FileSystemDirectoryLister"/> against real temp folders.</summary>
public class ScannerFileSystemTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly TempDir _data = new();
    private readonly TempDir _tree = new();
    private readonly StorageFixture _f;

    public ScannerFileSystemTests(ITestOutputHelper output)
    {
        _output = output;
        _f = new StorageFixture(_data);
    }

    private string RootPath => _tree.Path;

    public void Dispose()
    {
        _f.Dispose();
        _data.Dispose();
        ClearAttributes(_tree.Path);
        RemoveLinks(_tree.Path);
        _tree.Dispose();
    }

    private string P(string relative) => Path.Combine(RootPath, relative);

    private void WriteFile(string relative, int size = 10, DateTime? mtime = null)
    {
        string path = P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        System.IO.File.SetLastWriteTimeUtc(path, mtime ?? new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc));
    }

    private void BuildTree()
    {
        WriteFile(@"Projekte\Angebot Müller.pdf", 1234);
        WriteFile(@"Projekte\notes.txt", 5);
        WriteFile(@"Projekte\Alt\2019\report.docx", 77);
        WriteFile(@"Projekte\Alt\2019\leer.txt", 0);
        Directory.CreateDirectory(P(@"Projekte\Leerer Ordner"));
        WriteFile(@"Fotos\urlaub.jpg", 4096);
        WriteFile("top.txt", 1);

        // Excluded by the default rules.
        WriteFile(@"Code\node_modules\lib\index.js", 3);
        WriteFile(@"Code\.git\HEAD", 3);
        WriteFile(@"Code\main.cs", 30);
        WriteFile(@"Projekte\~$Angebot.docx", 1);
        WriteFile(@"Fotos\Thumbs.db", 1);
        WriteFile(@"Fotos\desktop.ini", 1);
        WriteFile(@"Projekte\scratch.tmp", 1);
        WriteFile(@"Projekte\system.dat", 1);
        System.IO.File.SetAttributes(P(@"Projekte\system.dat"), FileAttributes.System);
        WriteFile(@"SysDir\inner.txt", 1);
        new DirectoryInfo(P("SysDir")).Attributes |= FileAttributes.System;

        // Hidden without System is kept.
        WriteFile(@"Projekte\.hidden", 2);
        System.IO.File.SetAttributes(P(@"Projekte\.hidden"), FileAttributes.Hidden | FileAttributes.Archive);
    }

    private (RootInfo Root, Scanner Scanner) Setup(ExcludeRules rules, int batchSize = Scanner.DefaultBatchSize, IDirectoryLister? lister = null)
    {
        var root = _f.Store.AddRoot(RootPath, null, RootKind.Local);
        var scanner = new Scanner(_f.Store, lister ?? FileSystemDirectoryLister.Instance, rules, new ScannerOptions { BatchSize = batchSize });
        return (root, scanner);
    }

    [Fact]
    public void FirstScan_RecordsEverythingExceptExclusions()
    {
        BuildTree();
        var rules = ExcludeRules.CreateDefault();
        var (root, scanner) = Setup(rules);

        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        var expected = ScanAssert.WalkFileSystem(RootPath, rules);
        ScanAssert.TreeEquals(_f, root.Id, expected);
        var keys = expected.Keys.ToHashSet();
        Assert.Contains(@"Projekte\Alt\2019\report.docx", keys);
        Assert.Contains(@"Projekte\Leerer Ordner", keys);
        Assert.Contains(@"Projekte\.hidden", keys);
        Assert.Contains("Code", keys);
        Assert.Contains(@"Code\main.cs", keys);
        foreach (string excluded in new[]
        {
            @"Code\node_modules", @"Code\.git", @"Projekte\~$Angebot.docx", @"Fotos\Thumbs.db", @"Fotos\desktop.ini",
            @"Projekte\scratch.tmp", @"Projekte\system.dat", "SysDir",
        })
        {
            Assert.DoesNotContain(excluded, keys);
        }

        Assert.Equal(1234, ScanAssert.ReadTree(_f, root.Id)[@"Projekte\Angebot Müller.pdf"].State.Size);
        Assert.Equal(8, stats.Excluded);

        // The search finds the files through MemoryIndex and SearchEngine.
        Assert.Equal(new[] { P(@"Projekte\Angebot Müller.pdf") }, _f.Paths("angebot mueller"));
        Assert.Equal(new[] { P(@"Projekte\Alt\2019\report.docx") }, _f.Paths("2019 .docx"));
        Assert.Empty(_f.Paths("index.js"));
    }

    [Fact]
    public void SecondScan_AfterCreateDeleteRenameAndResize_MatchesFileSystemExactly()
    {
        BuildTree();
        var rules = ExcludeRules.CreateDefault();
        var (root, scanner) = Setup(rules);
        scanner.ScanRoot(root.Id);
        var before = ScanAssert.ReadTree(_f, root.Id);

        WriteFile(@"Projekte\neu.txt", 3);                                           // create file
        WriteFile(@"Neuer Ordner\Unterordner\tief.txt", 4);                          // create folders with content
        System.IO.File.Delete(P(@"Projekte\notes.txt"));                             // delete file
        Directory.Delete(P(@"Projekte\Alt"), recursive: true);                       // delete folder with subtree
        System.IO.File.Move(P("top.txt"), P("oben.txt"));                            // rename file
        Directory.Move(P("Fotos"), P("Bilder"));                                     // rename folder with content
        Directory.Move(P(@"Projekte\Leerer Ordner"), P(@"Projekte\tmp-case"));       // case-only rename (two steps)
        Directory.Move(P(@"Projekte\tmp-case"), P(@"Projekte\LEERER ORDNER"));
        System.IO.File.WriteAllBytes(P(@"Projekte\Angebot Müller.pdf"), new byte[2000]); // size change
        System.IO.File.SetLastWriteTimeUtc(P(@"Projekte\Angebot Müller.pdf"), new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        System.IO.File.SetLastWriteTimeUtc(P(@"Code\main.cs"), new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc)); // time change
        WriteFile(@"Code\node_modules\more.js");                                     // change in an excluded folder

        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        ScanAssert.TreeEquals(_f, root.Id, ScanAssert.WalkFileSystem(RootPath, rules));
        var after = ScanAssert.ReadTree(_f, root.Id);
        Assert.Equal(before[@"Projekte\Angebot Müller.pdf"].Id, after[@"Projekte\Angebot Müller.pdf"].Id);
        Assert.Equal(2000, after[@"Projekte\Angebot Müller.pdf"].State.Size);
        Assert.Equal(before[@"Projekte\Leerer Ordner"].Id, after[@"Projekte\LEERER ORDNER"].Id);

        // Search agrees, also after a restart (database and memory hold the same).
        Assert.Equal(new[] { P(@"Bilder\urlaub.jpg") }, _f.Paths("urlaub"));
        Assert.Empty(_f.Paths("notes"));
        Assert.Equal(new[] { P(@"Neuer Ordner\Unterordner\tief.txt") }, _f.Paths("tief"));
        _f.Restart();
        _f.Load();
        Assert.Equal(new[] { P(@"Bilder\urlaub.jpg") }, _f.Paths("urlaub"));
        Assert.Equal(new[] { P("oben.txt") }, _f.Paths("oben"));
        Assert.Empty(_f.Paths("2019"));
    }

    [Fact]
    public async Task JunctionLoop_IsRecordedButNotEntered()
    {
        WriteFile(@"A\B\file.txt");
        string loop = P(@"A\B\loop");
        if (!ScanAssert.TryCreateJunction(loop, RootPath))
        {
            _output.WriteLine("SKIPPED: mklink /J is not possible in this environment.");
            return;
        }

        using var target = new TempDir();
        System.IO.File.WriteAllText(Path.Combine(target.Path, "elsewhere.txt"), "x");
        string outside = P(@"A\outside");
        Assert.True(ScanAssert.TryCreateJunction(outside, target.Path));

        // The detector reads the tag without opening the folder and agrees with DirectoryInfo.LinkTarget.
        Assert.NotNull(new DirectoryInfo(loop).LinkTarget);
        Assert.Equal(0xA000_0003u, ReparseTagLinkDetector.TryGetReparseTag(loop)); // IO_REPARSE_TAG_MOUNT_POINT
        Assert.True(ReparseTagLinkDetector.Instance.IsLink(loop));
        Assert.True(ReparseTagLinkDetector.Instance.IsLink(loop + "\\"));
        Assert.Equal(0u, ReparseTagLinkDetector.TryGetReparseTag(P(@"A\B")));
        Assert.Null(ReparseTagLinkDetector.TryGetReparseTag(P("does-not-exist")));

        var rules = ExcludeRules.CreateDefault();
        var (root, scanner) = Setup(rules);
        var task = Task.Run(() => scanner.ScanRoot(root.Id));
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(60))));
        var stats = await task;

        Assert.Equal(ScanOutcome.Complete, stats.Outcome);
        Assert.Equal(2, stats.LinksNotEntered);
        Assert.Equal(3, stats.DirectoriesListed); // root, A, B
        var tree = ScanAssert.ReadTree(_f, root.Id);
        Assert.True(tree[@"A\B\loop"].State.IsDirectory);
        Assert.True((tree[@"A\B\loop"].State.Attributes & FileAttributes.ReparsePoint) != 0);
        Assert.DoesNotContain(tree.Keys, k => k.StartsWith(@"A\B\loop\", StringComparison.Ordinal) || k.StartsWith(@"A\outside\", StringComparison.Ordinal));
        ScanAssert.TreeEquals(_f, root.Id, ScanAssert.WalkFileSystem(RootPath, rules));

        // Second scan: stable.
        Assert.Equal(0, scanner.ScanRoot(root.Id).Inserted);
    }

    [Fact]
    public void CancelMidScan_ThenFullScan_MatchesFileSystem()
    {
        for (int d = 0; d < 6; d++)
        {
            for (int i = 0; i < 25; i++)
            {
                WriteFile($@"Dir{d}\Sub{i % 3}\file{i}.txt", i);
            }
        }

        var rules = ExcludeRules.CreateDefault();
        using var cts = new CancellationTokenSource();
        var lister = new CallbackLister(FileSystemDirectoryLister.Instance, n =>
        {
            if (n == 5)
            {
                cts.Cancel();
            }
        });
        var (root, scanner) = Setup(rules, batchSize: 10, lister);

        var stats = scanner.ScanRoot(root.Id, cts.Token);

        Assert.Equal(ScanOutcome.Cancelled, stats.Outcome);
        Assert.Equal(ScanStatus.Aborted, _f.Store.GetRoot(root.Id)!.LastScanStatus);
        var expected = ScanAssert.WalkFileSystem(RootPath, rules);
        var partial = ScanAssert.ReadTree(_f, root.Id);
        Assert.InRange(partial.Count, 1, expected.Count - 1);
        foreach (var (path, row) in partial)
        {
            Assert.Equal(expected[path], row.State);
        }

        Assert.Equal(ScanAssert.TotalEntries(_f), _f.Index.Count);

        var full = new Scanner(_f.Store, FileSystemDirectoryLister.Instance, rules);
        Assert.Equal(ScanOutcome.Complete, full.ScanRoot(root.Id).Outcome);
        ScanAssert.TreeEquals(_f, root.Id, expected);
    }

    [Fact]
    public void PathRuleWithEnvironmentVariable_ExcludesFolder()
    {
        BuildTree();
        string variable = "FD_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, RootPath);
        try
        {
            var rules = new ExcludeRules([], [$@"%{variable}%\Projekte\*"], []);
            var (root, scanner) = Setup(rules);
            scanner.ScanRoot(root.Id);

            var tree = ScanAssert.ReadTree(_f, root.Id);
            Assert.Contains("Projekte", tree.Keys);
            Assert.DoesNotContain(tree.Keys, k => k.StartsWith(@"Projekte\", StringComparison.Ordinal));
            Assert.Contains(@"Fotos\urlaub.jpg", tree.Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void RootMissing_ChangesNothing()
    {
        BuildTree();
        var (root, scanner) = Setup(ExcludeRules.CreateDefault());
        scanner.ScanRoot(root.Id);
        string snapshot = ScanAssert.DatabaseSnapshot(_f);

        ClearAttributes(RootPath);
        Directory.Delete(RootPath, recursive: true);
        var stats = scanner.ScanRoot(root.Id);

        Assert.Equal(ScanOutcome.RootUnreachable, stats.Outcome);
        Assert.Equal(snapshot, ScanAssert.DatabaseSnapshot(_f));
    }

    [Fact]
    public void Lister_ReturnsAllEntriesWithData_AndThrowsForMissingFolder()
    {
        WriteFile("a.txt", 42, new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        WriteFile(@"sub\b.txt");
        WriteFile("hidden.txt");
        System.IO.File.SetAttributes(P("hidden.txt"), FileAttributes.Hidden | FileAttributes.System);

        var list = FileSystemDirectoryLister.Instance.List(RootPath).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(new[] { "a.txt", "hidden.txt", "sub" }, list.Select(e => e.Name));
        Assert.Equal(42, list[0].Size);
        Assert.False(list[0].IsDirectory);
        Assert.Equal(new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc), list[0].LastWriteUtc);
        Assert.Equal(DateTimeKind.Utc, list[0].LastWriteUtc.Kind);
        Assert.True((list[1].Attributes & FileAttributes.System) != 0); // nothing skipped by the lister
        Assert.True(list[2].IsDirectory);
        Assert.Equal(0, list[2].Size);

        Assert.Throws<DirectoryNotFoundException>(() => FileSystemDirectoryLister.Instance.List(P("missing")));
        Assert.ThrowsAny<IOException>(() => FileSystemDirectoryLister.Instance.List(P("a.txt")));
    }

    /// <summary>
    /// Removes junctions without following them. Needed because the recursive delete of .NET first calls
    /// DeleteVolumeMountPoint on a junction, which fails without admin rights and makes the delete throw.
    /// </summary>
    private static void RemoveLinks(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var dir in new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                dir.Delete(); // non-recursive: removes only the link
            }
            else
            {
                RemoveLinks(dir.FullName);
            }
        }
    }

    private static void ClearAttributes(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            if ((info.Attributes & (FileAttributes.System | FileAttributes.ReadOnly)) != 0)
            {
                info.Attributes &= ~(FileAttributes.System | FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>Wraps a lister and reports the number of each call.</summary>
    private sealed class CallbackLister(IDirectoryLister inner, Action<int> onCall) : IDirectoryLister
    {
        private int _calls;

        public IReadOnlyList<RawEntry> List(string directoryPath)
        {
            var result = inner.List(directoryPath);
            onCall(++_calls);
            return result;
        }
    }
}
