using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FileDetective.Core;

namespace FileDetective.Tests;

public class UpdateInstallerTests : IDisposable
{
    private const string Repo = "someone/FileDetective";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fd-install-" + Guid.NewGuid().ToString("N"));
    private readonly string _appFolder;
    private readonly string _exe;

    public UpdateInstallerTests()
    {
        _appFolder = Path.Combine(_root, "app");
        Directory.CreateDirectory(_appFolder);
        _exe = Path.Combine(_appFolder, "FileDetective.exe");
        File.WriteAllBytes(_exe, MakeExe("old"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] MakeExe(string marker)
    {
        byte[] data = new byte[4096];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        Encoding.ASCII.GetBytes(marker).CopyTo(data, 100);
        return data;
    }

    private static byte[] MakeZip(params (string Name, byte[] Data)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        }

        return stream.ToArray();
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static UpdateInfo Info() => new(
        "0.2.0", "v0.2.0", $"https://github.com/{Repo}/releases/tag/v0.2.0",
        $"https://github.com/{Repo}/releases/download/v0.2.0/FileDetective-0.2.0-win-x64.zip",
        $"https://github.com/{Repo}/releases/download/v0.2.0/SHA256SUMS.txt");

    private static FakeHttpHandler Release(byte[] zip, string sums)
        => new(request => request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sums) }
            : FakeHttpHandler.Bytes(zip));

    private UpdateInstaller NewInstaller(FakeHttpHandler handler) => new(Path.Combine(_root, "work"), handler, "0.1.0");

    [Fact]
    public async Task GoodChecksum_ReplacesTheExe_AndKeepsTheOldOne()
    {
        byte[] newExe = MakeExe("new");
        byte[] zip = MakeZip(("FileDetective.exe", newExe), ("README.md", Encoding.UTF8.GetBytes("readme")));
        string sums = $"{Sha(zip)}  FileDetective-0.2.0-win-x64.zip\n{new string('a', 64)}  FileDetective-0.2.0-win-x64-setup.exe\n";
        var handler = Release(zip, sums);
        using var installer = NewInstaller(handler);

        var result = await installer.InstallAsync(Info(), _exe);

        Assert.Equal(UpdateInstallStatus.Installed, result.Status);
        Assert.Equal(newExe, File.ReadAllBytes(_exe));
        Assert.Equal(MakeExe("old"), File.ReadAllBytes(_exe + ".old"));
        Assert.False(File.Exists(_exe + ".new"));
        Assert.All(handler.Requests, r => Assert.Equal("FileDetective/0.1.0", r.Headers.UserAgent.ToString()));

        // The next start removes the .old file.
        UpdateInstaller.CleanupOld(_exe);
        Assert.False(File.Exists(_exe + ".old"));
        Assert.True(File.Exists(_exe));
    }

    [Fact]
    public async Task WrongChecksum_StopsAndChangesNothing()
    {
        byte[] zip = MakeZip(("FileDetective.exe", MakeExe("new")));
        var handler = Release(zip, $"{new string('0', 64)}  FileDetective-0.2.0-win-x64.zip\n");
        using var installer = NewInstaller(handler);

        var result = await installer.InstallAsync(Info(), _exe);

        Assert.Equal(UpdateInstallStatus.ChecksumMismatch, result.Status);
        Assert.Equal(MakeExe("old"), File.ReadAllBytes(_exe));
        Assert.False(File.Exists(_exe + ".old"));
        Assert.False(File.Exists(_exe + ".new"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "work"))); // the downloaded ZIP is deleted
    }

    [Fact]
    public async Task ChecksumFileWithoutALineForTheZip_Stops()
    {
        byte[] zip = MakeZip(("FileDetective.exe", MakeExe("new")));
        using var installer = NewInstaller(Release(zip, $"{Sha(zip)}  SomethingElse.zip\n"));

        Assert.Equal(UpdateInstallStatus.ChecksumMismatch, (await installer.InstallAsync(Info(), _exe)).Status);
        Assert.Equal(MakeExe("old"), File.ReadAllBytes(_exe));
    }

    [Fact]
    public async Task ChecksumLine_AcceptsBinaryMarkerCrLfAndUpperCase()
    {
        byte[] zip = MakeZip(("FileDetective.exe", MakeExe("new")));
        using var installer = NewInstaller(Release(zip, "﻿" + Sha(zip).ToUpperInvariant() + " *FileDetective-0.2.0-win-x64.zip\r\n"));

        Assert.Equal(UpdateInstallStatus.Installed, (await installer.InstallAsync(Info(), _exe)).Status);
    }

    [Fact]
    public async Task ZipWithoutTheExe_FailsAndChangesNothing()
    {
        byte[] zip = MakeZip(("README.md", Encoding.UTF8.GetBytes("no exe in here")), ("sub/FileDetective.exe", MakeExe("nested")));
        using var installer = NewInstaller(Release(zip, $"{Sha(zip)}  FileDetective-0.2.0-win-x64.zip\n"));

        Assert.Equal(UpdateInstallStatus.ExtractFailed, (await installer.InstallAsync(Info(), _exe)).Status);
        Assert.Equal(MakeExe("old"), File.ReadAllBytes(_exe));
        Assert.False(File.Exists(_exe + ".new"));
    }

    [Fact]
    public async Task ExeThatIsNotAnExe_FailsAndChangesNothing()
    {
        byte[] zip = MakeZip(("FileDetective.exe", new byte[4096]));
        using var installer = NewInstaller(Release(zip, $"{Sha(zip)}  FileDetective-0.2.0-win-x64.zip\n"));

        Assert.Equal(UpdateInstallStatus.ExtractFailed, (await installer.InstallAsync(Info(), _exe)).Status);
        Assert.Equal(MakeExe("old"), File.ReadAllBytes(_exe));
    }

    [Fact]
    public async Task DownloadError_IsReported_AndChangesNothing()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("connection reset"));
        using var installer = NewInstaller(handler);

        Assert.Equal(UpdateInstallStatus.DownloadFailed, (await installer.InstallAsync(Info(), _exe)).Status);
        Assert.Equal(MakeExe("old"), File.ReadAllBytes(_exe));
    }

    [Fact]
    public async Task HttpNotFound_IsADownloadFailure()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var installer = NewInstaller(handler);

        Assert.Equal(UpdateInstallStatus.DownloadFailed, (await installer.InstallAsync(Info(), _exe)).Status);
    }

    [Fact]
    public async Task ReleaseWithoutDownloads_IsNotSupported_AndMakesNoRequest()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var installer = NewInstaller(handler);

        var result = await installer.InstallAsync(Info() with { ChecksumsUrl = null }, _exe);

        Assert.Equal(UpdateInstallStatus.NotSupported, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void DevelopmentBuild_ThatHasItsDllNextToTheExe_CannotReplaceItself()
    {
        Assert.True(UpdateInstaller.CanReplace(_exe));
        File.WriteAllText(Path.Combine(_appFolder, "FileDetective.dll"), "x");
        Assert.False(UpdateInstaller.CanReplace(_exe));
        Assert.False(UpdateInstaller.CanReplace(Path.Combine(_appFolder, "missing.exe")));
        Assert.False(UpdateInstaller.CanReplace(null));
    }

    [Fact]
    public void ProgramFilesStyleFolder_IsNotWritable()
    {
        // The folder of the Windows system files is not writable without administrator rights.
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!Directory.Exists(system) || UpdateInstaller.FolderIsWritable(system))
        {
            return; // run elevated: nothing to check
        }

        Assert.False(UpdateInstaller.FolderIsWritable(system));
        Assert.True(UpdateInstaller.FolderIsWritable(_appFolder));
    }

    [Fact]
    public void CleanupOld_IsQuietWhenThereIsNothing()
    {
        UpdateInstaller.CleanupOld(_exe);
        UpdateInstaller.CleanupOld(null);
        UpdateInstaller.CleanupOld(Path.Combine(_root, "nowhere", "x.exe"));
        Assert.True(File.Exists(_exe));
    }
}
