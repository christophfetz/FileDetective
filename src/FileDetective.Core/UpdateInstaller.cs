using System.IO.Compression;
using System.Security.Cryptography;

namespace FileDetective.Core;

public enum UpdateInstallStatus
{
    /// <summary>The new exe is in place; the old one is <c>&lt;exe&gt;.old</c>. The caller restarts the program.</summary>
    Installed,

    /// <summary>The release lacks a download, or the program does not run from a replaceable exe.</summary>
    NotSupported,

    DownloadFailed,

    /// <summary>The ZIP does not match <c>SHA256SUMS.txt</c> (or the file has no line for it). Nothing was changed.</summary>
    ChecksumMismatch,

    /// <summary>The ZIP does not contain a usable exe.</summary>
    ExtractFailed,

    /// <summary>Renaming or placing the files failed; the old exe is back in its place.</summary>
    ReplaceFailed,
}

public sealed record UpdateInstallResult(UpdateInstallStatus Status, string? Detail = null);

/// <summary>
/// Update per click (PLAN.md 9): loads the ZIP and <c>SHA256SUMS.txt</c> of the release, compares the checksum (a
/// difference stops everything), takes the exe out of the ZIP, renames the running exe to <c>.old</c>, puts the new one
/// in its place. The caller then starts the new exe and ends; <see cref="CleanupOld"/> removes the <c>.old</c> file at
/// the next start. Only the data folder and the folder of the exe are written. The HTTP handler is injected for tests.
/// </summary>
public sealed class UpdateInstaller : IDisposable
{
    /// <summary>Name of the exe inside the release ZIP.</summary>
    public const string ExeNameInZip = "FileDetective.exe";

    private const long MaxZipBytes = 300L * 1024 * 1024;
    private const long MaxSumsBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _workFolder;

    /// <param name="workFolder">Folder for the downloads, below the data folder; created as needed.</param>
    /// <param name="handler">HTTP handler; null for the real network. Not disposed by the installer.</param>
    /// <param name="currentVersion">For the user agent <c>FileDetective/&lt;version&gt;</c>.</param>
    public UpdateInstaller(string workFolder, HttpMessageHandler? handler = null, string currentVersion = "0.0.0")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workFolder);
        _workFolder = workFolder;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(10);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FileDetective/" + (AppVersion.TryParse(currentVersion, out var v) ? v.ToString() : "0.0.0"));
    }

    /// <summary>
    /// True if an in-place update can work: the exe is a bundled single file (no <c>FileDetective.dll</c> next to it, so
    /// not a development build) and its folder is writable for the current user.
    /// </summary>
    public static bool CanReplace(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return false;
        }

        string? folder = Path.GetDirectoryName(exePath);
        if (folder is null || File.Exists(Path.Combine(folder, "FileDetective.dll")))
        {
            return false;
        }

        return FolderIsWritable(folder);
    }

    internal static bool FolderIsWritable(string folder)
    {
        string probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>Deletes the <c>.old</c> file a former update left next to the exe. Never throws; a file still in use stays for the next start.</summary>
    public static void CleanupOld(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        try
        {
            string old = exePath + ".old";
            if (File.Exists(old))
            {
                File.Delete(old);
                FileLog.Shared.Info($"Removed {old}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Shared.Info($"The old exe could not be removed yet ({ex.Message}).");
        }
    }

    /// <summary>
    /// Downloads, verifies and replaces. Never throws except for cancellation. On every failure the running exe is
    /// left as it was.
    /// </summary>
    public async Task<UpdateInstallResult> InstallAsync(UpdateInfo info, string exePath, CancellationToken cancellation = default)
    {
        if (!info.CanInstall || !CanReplace(exePath))
        {
            return new UpdateInstallResult(UpdateInstallStatus.NotSupported);
        }

        string zipPath = Path.Combine(_workFolder, info.ZipName);
        string newExe = exePath + ".new";
        string oldExe = exePath + ".old";
        try
        {
            Directory.CreateDirectory(_workFolder);

            string sums;
            try
            {
                sums = await DownloadTextAsync(info.ChecksumsUrl!, cancellation).ConfigureAwait(false);
                await DownloadFileAsync(info.ZipUrl!, zipPath, cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidDataException)
            {
                cancellation.ThrowIfCancellationRequested();
                FileLog.Shared.Info($"Update download failed: {ex.Message}");
                return new UpdateInstallResult(UpdateInstallStatus.DownloadFailed, ex.Message);
            }

            string? expected = FindChecksum(sums, info.ZipName);
            string actual;
            await using (var stream = File.OpenRead(zipPath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
            }

            if (expected is null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                FileLog.Shared.Error($"Update stopped: checksum of {info.ZipName} does not match SHA256SUMS.txt.");
                return new UpdateInstallResult(UpdateInstallStatus.ChecksumMismatch);
            }

            try
            {
                ExtractExe(zipPath, newExe);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                TryDelete(newExe);
                FileLog.Shared.Error("Update stopped: the exe could not be taken out of the ZIP", ex);
                return new UpdateInstallResult(UpdateInstallStatus.ExtractFailed, ex.Message);
            }

            try
            {
                TryDelete(oldExe);
                File.Move(exePath, oldExe, overwrite: true);
                try
                {
                    File.Move(newExe, exePath);
                }
                catch
                {
                    File.Move(oldExe, exePath, overwrite: true); // put the running exe back
                    throw;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(newExe);
                FileLog.Shared.Error("Update stopped: replacing the exe failed", ex);
                return new UpdateInstallResult(UpdateInstallStatus.ReplaceFailed, ex.Message);
            }

            FileLog.Shared.Info($"Updated to {info.Version}; the old exe is {oldExe}.");
            return new UpdateInstallResult(UpdateInstallStatus.Installed);
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    /// <summary>The hash for <paramref name="fileName"/> from a <c>sha256sum</c>-style file (<c>hash  name</c>, optional <c>*</c> before the name); null if absent.</summary>
    internal static string? FindChecksum(string sums, string fileName)
    {
        foreach (string raw in sums.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space != 64 || !line[..64].All(char.IsAsciiHexDigit))
            {
                continue;
            }

            string name = line[64..].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return line[..64];
            }
        }

        return null;
    }

    private static void ExtractExe(string zipPath, string destination)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        // Only the entry with this exact name is read, and it is written to a fixed path: nothing in the ZIP can choose
        // where a file goes.
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(ExeNameInZip, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"The ZIP contains no {ExeNameInZip}.");
        if (entry.Length < 1024)
        {
            throw new InvalidDataException("The exe in the ZIP is too small.");
        }

        using (var input = entry.Open())
        using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
        }

        using var check = File.OpenRead(destination);
        if (check.ReadByte() != 'M' || check.ReadByte() != 'Z')
        {
            throw new InvalidDataException("The file in the ZIP is not an exe.");
        }
    }

    private async Task<string> DownloadTextAsync(string url, CancellationToken cancellation)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxSumsBytes)
        {
            throw new InvalidDataException("SHA256SUMS.txt is too large.");
        }

        return await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
    }

    private async Task DownloadFileAsync(string url, string path, CancellationToken cancellation)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxZipBytes)
        {
            throw new InvalidDataException("The ZIP is too large.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxZipBytes)
            {
                throw new InvalidDataException("The ZIP is too large.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    public void Dispose() => _http.Dispose();
}
