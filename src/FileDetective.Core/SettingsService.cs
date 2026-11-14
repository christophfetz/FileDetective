using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDetective.Core;

/// <summary>How <see cref="SettingsService.Load"/> found the file.</summary>
public enum SettingsLoadResult
{
    /// <summary>Read normally.</summary>
    Loaded,

    /// <summary>There is no file yet: defaults, nothing written.</summary>
    Missing,

    /// <summary>The file was unreadable; it was renamed (<see cref="SettingsService.BackupPath"/>) and defaults apply.</summary>
    CorruptRenamed,
}

/// <summary>
/// Reads and writes <c>settings.json</c> (PLAN.md 8.5). Never throws when loading: a missing file gives the defaults, a
/// damaged file is renamed to <c>settings.json.bak-timestamp</c> and the defaults apply. Writing is atomic
/// (temporary file in the same folder, then replace), so a crash never leaves half a file.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private AppSettings _current = new();

    public SettingsService(string path, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
        _clock = clock ?? TimeProvider.System;
    }

    public string FilePath { get; }

    /// <summary>Where the damaged file went, or null.</summary>
    public string? BackupPath { get; private set; }

    /// <summary>The settings in force (the defaults until <see cref="Load"/> ran).</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after <see cref="Update"/> or <see cref="Save"/> wrote new settings (on the calling thread).</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>
    /// Reads the file. The file is never created here: a first start writes nothing until something is saved.
    /// A file from a newer program version (higher <c>schemaVersion</c>) is read as far as the known fields go, and its
    /// version number is kept when saving.
    /// </summary>
    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            BackupPath = null;
            if (!File.Exists(FilePath))
            {
                _current = new AppSettings();
                return SettingsLoadResult.Missing;
            }

            try
            {
                // Editors such as Notepad write a UTF-8 byte order mark; the JSON reader refuses it, so it is skipped.
                ReadOnlySpan<byte> bytes = File.ReadAllBytes(FilePath);
                if (bytes.StartsWith(Encoding.UTF8.Preamble))
                {
                    bytes = bytes[Encoding.UTF8.Preamble.Length..];
                }

                var parsed = JsonSerializer.Deserialize<AppSettings>(bytes, Options)
                    ?? throw new JsonException("The file contains null.");
                if (parsed.SchemaVersion < 1)
                {
                    throw new JsonException("Invalid schemaVersion.");
                }

                _current = parsed.Sanitized();
                return SettingsLoadResult.Loaded;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _current = new AppSettings();
                BackupPath = TryRename();
                return SettingsLoadResult.CorruptRenamed;
            }
        }
    }

    /// <summary>Writes the settings atomically and makes them current. Throws <see cref="IOException"/> if writing fails.</summary>
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clean = settings.Sanitized();
        lock (_gate)
        {
            Write(clean);
            _current = clean;
        }

        Changed?.Invoke(clean);
    }

    /// <summary>Applies a change to the current settings and saves the result.</summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings next;
        lock (_gate)
        {
            next = change(_current).Sanitized();
            Write(next);
            _current = next;
        }

        Changed?.Invoke(next);
        return next;
    }

    private void Write(AppSettings settings)
    {
        string? folder = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
        try
        {
            if (File.Exists(FilePath))
            {
                File.Replace(temp, FilePath, null);
            }
            else
            {
                File.Move(temp, FilePath);
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (IOException)
            {
                // best effort
            }

            throw;
        }
    }

    private string? TryRename()
    {
        try
        {
            string stamp = _clock.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string target = $"{FilePath}.bak-{stamp}";
            for (int n = 2; File.Exists(target); n++)
            {
                target = $"{FilePath}.bak-{stamp}-{n}";
            }

            File.Move(FilePath, target);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not move it away: the defaults still apply; the next save replaces the file.
            return null;
        }
    }
}
