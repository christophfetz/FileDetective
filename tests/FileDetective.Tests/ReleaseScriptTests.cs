using System.Diagnostics;
using System.Text;

namespace FileDetective.Tests;

/// <summary>
/// Release publishing (PLAN.md stage 8): the script that cuts the notes of one version out of CHANGELOG.md, the version
/// in the project file and the files the workflow and the installer rely on. The script runs in Windows PowerShell 5.1,
/// which exists on every Windows machine (the workflow runs it in PowerShell 7).
/// </summary>
public class ReleaseScriptTests : IDisposable
{
    private static readonly string Root = FindRoot();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fd-notes-" + Guid.NewGuid().ToString("N"));

    public ReleaseScriptTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FileDetective.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private (int ExitCode, string Output, string Error) Run(string changelogText, string version, out string notesPath)
    {
        string changelog = Path.Combine(_folder, "CHANGELOG.md");
        notesPath = Path.Combine(_folder, "notes.md");
        File.WriteAllText(changelog, changelogText, new UTF8Encoding(false));
        string script = Path.Combine(Root, ".github", "scripts", "Get-ReleaseNotes.ps1");

        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _folder,
        };
        foreach (string arg in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-ChangelogPath", changelog, "-Version", version, "-OutputPath", notesPath,
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60_000), "the script did not finish");
        return (process.ExitCode, output, error);
    }

    private const string Sample = """
        # Changelog

        ## 0.2.0

        - Newer: something äöü "quoted".
        - Another change.

        ## 0.1.0 (2026-11-20)

        - First release.
          - nested line

        ## 0.0.9

        - Old stuff.
        """;

    [Fact]
    public void CutsOutExactlyTheSectionOfTheVersion()
    {
        var (code, _, error) = Run(Sample, "0.2.0", out string notes);
        Assert.True(code == 0, error);
        string text = File.ReadAllText(notes);
        Assert.Contains("Newer: something äöü \"quoted\".", text);
        Assert.Contains("Another change.", text);
        Assert.DoesNotContain("First release", text);
        Assert.DoesNotContain("## ", text);
        Assert.False(text.StartsWith('﻿'), "no byte order mark");
    }

    [Fact]
    public void MiddleSection_WithDateInTheHeading_AndTagPrefix()
    {
        var (code, _, error) = Run(Sample, "v0.1.0", out string notes);
        Assert.True(code == 0, error);
        string text = File.ReadAllText(notes);
        Assert.Contains("First release.", text);
        Assert.Contains("  - nested line", text);
        Assert.DoesNotContain("Newer", text);
        Assert.DoesNotContain("Old stuff", text);
    }

    [Fact]
    public void LastSection_RunsToTheEndOfTheFile()
    {
        var (code, _, error) = Run(Sample, "0.0.9", out string notes);
        Assert.True(code == 0, error);
        Assert.Equal("- Old stuff.", File.ReadAllText(notes).Trim());
    }

    [Fact]
    public void VersionPrefix_IsNotMistakenForAnotherVersion()
    {
        // "## 0.1.0" must not be found when asking for "0.1".
        var (code, _, _) = Run(Sample, "0.1", out string notes);
        Assert.NotEqual(0, code);
        Assert.False(File.Exists(notes));
    }

    [Fact]
    public void MissingSection_Fails()
    {
        var (code, _, _) = Run(Sample, "9.9.9", out string notes);
        Assert.NotEqual(0, code);
        Assert.False(File.Exists(notes));
    }

    [Fact]
    public void EmptySection_Fails()
    {
        var (code, _, _) = Run("# Changelog\n\n## 0.3.0\n\n## 0.2.0\n\n- x\n", "0.3.0", out string notes);
        Assert.NotEqual(0, code);
        Assert.False(File.Exists(notes));
    }

    [Fact]
    public void TheRealChangelog_HasASectionForTheProjectVersion()
    {
        string csproj = File.ReadAllText(Path.Combine(Root, "src", "FileDetective.App", "FileDetective.App.csproj"));
        var match = System.Text.RegularExpressions.Regex.Match(csproj, @"<Version>([^<]+)</Version>");
        Assert.True(match.Success, "the project has a <Version>");
        string version = match.Groups[1].Value.Trim();
        Assert.True(FileDetective.Core.AppVersion.TryParse(version, out _), $"'{version}' is a valid version");

        var (code, _, error) = Run(File.ReadAllText(Path.Combine(Root, "CHANGELOG.md")), version, out string notes);
        Assert.True(code == 0, $"CHANGELOG.md has no section '## {version}': {error}");
        Assert.True(new FileInfo(notes).Length > 20);
    }

    [Fact]
    public void ReleaseFiles_ExistAndAgreeOnNames()
    {
        string workflow = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
        Assert.Contains("tags: ['v*']", workflow);
        Assert.Contains("SHA256SUMS.txt", workflow);
        Assert.Contains("FileDetective-$version-win-x64.zip", workflow);
        Assert.Contains("-win-x64-setup.exe", workflow);

        string iss = File.ReadAllText(Path.Combine(Root, "installer", "FileDetective.iss"));
        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.Contains("{localappdata}\\Programs\\{#AppName}", iss);
        Assert.Contains("OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup", iss);
        // Same value name and arguments as AutostartManager, so the program sees the entry as its own.
        Assert.Contains($"ValueName: \"{FileDetective.Core.AutostartManager.DefaultValueName}\"", iss);
        Assert.Contains($"\"\" {FileDetective.Core.AutostartManager.Argument}\"", iss);
        Assert.Contains("AppMutex=" + FileDetective.Core.SingleInstanceGuard.RunningMarkerName, iss);

        // The README links to the same repository as the program (one placeholder, replaced together).
        string readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        Assert.Contains("https://github.com/" + FileDetective.Core.UpdateChecker.Repository + "/releases/latest", readme);

        Assert.Equal("FileDetective-0.2.0-win-x64.zip", FileDetective.Core.UpdateChecker.ZipName("0.2.0"));
    }
}
