using FileDetective.Core;
using Xunit.Abstractions;

namespace FileDetective.Tests;

/// <summary>
/// Measures a real scan of a large local folder, read-only; the index is written to a temp folder and deleted
/// afterwards. Runs only when the environment variable <c>FD_MEASURE_SCAN_PATH</c> names the folder, e.g.:
/// <c>$env:FD_MEASURE_SCAN_PATH=$env:USERPROFILE; dotnet test -c Release --filter Category=Measurement</c>.
/// <c>FD_MEASURE_NO_EXCLUDES=1</c> scans without exclusions.
/// </summary>
[Trait("Category", "Measurement")]
public class ScanMeasurementTests(ITestOutputHelper output)
{
    [Fact]
    public void ScanLargeLocalFolder()
    {
        string? path = Environment.GetEnvironmentVariable("FD_MEASURE_SCAN_PATH");
        if (string.IsNullOrEmpty(path))
        {
            output.WriteLine("SKIPPED: FD_MEASURE_SCAN_PATH is not set.");
            return;
        }

        using var dir = new TempDir();
        using var f = new StorageFixture(dir);
        var root = f.Store.AddRoot(path, null, RootKind.Local);
        // FD_MEASURE_NO_EXCLUDES=1 measures without exclusions (more entries, e.g. against the 500,000-entry target of PLAN.md 6.5).
        bool noExcludes = Environment.GetEnvironmentVariable("FD_MEASURE_NO_EXCLUDES") == "1";
        var rules = noExcludes ? ExcludeRules.None : ExcludeRules.CreateDefault();
        output.WriteLine($"Scanning {path} ({(noExcludes ? "no exclusions" : "default exclusions")})");
        var scanner = new Scanner(f.Store, FileSystemDirectoryLister.Instance, rules);

        var first = scanner.ScanRoot(root.Id);
        Report("First scan", first);
        Assert.NotEqual(ScanOutcome.RootUnreachable, first.Outcome);

        var second = scanner.ScanRoot(root.Id);
        Report("Second scan (comparison)", second);
        output.WriteLine($"Index entries: {f.Index.Count:N0}");
    }

    private void Report(string title, ScanStatistics s)
        => output.WriteLine(
            $"{title}: {s.Outcome}, {s.DirectoriesListed:N0} directories, {s.Entries:N0} entries, {s.Excluded:N0} excluded, " +
            $"{s.LinksNotEntered} links, {s.FailedDirectories} failed, {s.Duration.TotalSeconds:F1} s, pauses {s.TotalPause.TotalMilliseconds:F0} ms, " +
            $"+{s.Inserted:N0} ~{s.Updated:N0} -{s.Deleted:N0}");
}
