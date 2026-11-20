using System.Text;
using FileDetective.Core;

namespace FileDetective.Tests;

public class FileLogTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string LogPath => Path.Combine(_dir.Path, "sub", "test.log");

    [Fact]
    public void WritesLinesWithLevelAndTimestamp_AndCreatesTheFolder()
    {
        var log = new FileLog(LogPath);
        log.Info("hello");
        log.Error("bad", new InvalidOperationException("boom"));

        string[] lines = File.ReadAllLines(LogPath);
        Assert.Contains("INFO  hello", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} INFO  hello$", lines[0]);
        Assert.Contains("ERROR bad: System.InvalidOperationException: boom", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NoBom_AndUmlautsSurvive()
    {
        var log = new FileLog(LogPath);
        log.Info("Müller ß");
        byte[] bytes = File.ReadAllBytes(LogPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("Müller ß", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Rotates_AtTheLimit_KeepsOneOlderFile()
    {
        var log = new FileLog(LogPath, maxBytes: 600);
        for (int i = 0; i < 40; i++)
        {
            log.Info($"line number {i:D3} with some padding to take space");
        }

        Assert.True(File.Exists(LogPath));
        Assert.True(File.Exists(log.OldPath));
        Assert.True(new FileInfo(LogPath).Length <= 600);
        Assert.True(new FileInfo(log.OldPath!).Length <= 600);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(LogPath)!), f => f.EndsWith(".old", StringComparison.Ordinal));

        // The newest line is in the current file, and old lines are gone after several rotations.
        string current = File.ReadAllText(LogPath);
        Assert.Contains("line number 039", current, StringComparison.Ordinal);
        Assert.DoesNotContain("line number 000", current + File.ReadAllText(log.OldPath!), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingBigFile_IsRotatedOnTheFirstWrite()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, new string('x', 5000));
        var log = new FileLog(LogPath, maxBytes: 1000);
        log.Info("fresh");

        Assert.Contains("fresh", File.ReadAllText(LogPath), StringComparison.Ordinal);
        Assert.DoesNotContain("xxxxx", File.ReadAllText(LogPath), StringComparison.Ordinal);
        Assert.Equal(5000, new FileInfo(log.OldPath!).Length);
    }

    [Fact]
    public void LineLongerThanTheLimit_IsStillWritten()
    {
        var log = new FileLog(LogPath, maxBytes: 100);
        log.Info(new string('y', 500));
        log.Info("next");

        Assert.Contains("next", File.ReadAllText(LogPath), StringComparison.Ordinal);
        Assert.Contains("yyyy", File.ReadAllText(log.OldPath!), StringComparison.Ordinal);
    }

    [Fact]
    public void NeverThrows_WhenThePathIsUnusable()
    {
        // A file where a folder is needed, a path that is a directory, and a log without path.
        string blocker = Path.Combine(_dir.Path, "blocker");
        File.WriteAllText(blocker, "x");
        new FileLog(Path.Combine(blocker, "x.log")).Info("a");
        Directory.CreateDirectory(Path.Combine(_dir.Path, "isdir"));
        new FileLog(Path.Combine(_dir.Path, "isdir")).Error("b", new Exception("c"));
        new FileLog(null).Info("nothing");
        new FileLog("").Error("nothing");
        FileLog.Shared.Info("the shared log without file does nothing either");
    }

    [Fact]
    public void KeepsWorking_WhenTheFileIsOpenElsewhere()
    {
        var log = new FileLog(LogPath);
        log.Info("one");
        using (var reader = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            log.Info("two");
        }

        string text = File.ReadAllText(LogPath);
        Assert.Contains("one", text, StringComparison.Ordinal);
        Assert.Contains("two", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ManyThreads_WriteEveryLineWholeAndTheFilesStayBounded()
    {
        var log = new FileLog(LogPath, maxBytes: 20_000);
        const int threads = 8;
        const int perThread = 400;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                log.Info($"thread {t} message {i:D4} END");
            }
        });

        string[] all = [.. File.ReadAllLines(LogPath), .. File.ReadAllLines(log.OldPath!)];
        Assert.All(all, line => Assert.EndsWith(" END", line, StringComparison.Ordinal)); // no torn or mixed lines
        Assert.True(new FileInfo(LogPath).Length <= 20_000);
        Assert.True(new FileInfo(log.OldPath!).Length <= 20_000);
    }

    [Fact]
    public void Initialize_MakesTheSharedLogWriteToFile()
    {
        var before = FileLog.Shared;
        try
        {
            var log = FileLog.Initialize(LogPath);
            Assert.Same(log, FileLog.Shared);
            FileLog.Shared.Info("via shared");

            // While the shared log points here, code under test in other classes running in parallel may append to the
            // file, so it is read without blocking writers (File.ReadAllText fails with "used by another process").
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Assert.Contains("via shared", reader.ReadToEnd(), StringComparison.Ordinal);
        }
        finally
        {
            typeof(FileLog).GetProperty(nameof(FileLog.Shared))!.SetValue(null, before);
        }
    }
}
