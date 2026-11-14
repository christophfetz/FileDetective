using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileDetective.Core;

/// <summary>A newer release found on GitHub (PLAN.md 9).</summary>
/// <param name="Version">Version text without the leading <c>v</c>.</param>
/// <param name="Tag">Tag of the release, e.g. <c>v0.2.0</c>.</param>
/// <param name="ReleaseUrl">Page of the release in the browser.</param>
/// <param name="ZipUrl">Download of <c>FileDetective-&lt;version&gt;-win-x64.zip</c>, or null if the release has none.</param>
/// <param name="ChecksumsUrl">Download of <c>SHA256SUMS.txt</c>, or null.</param>
public sealed record UpdateInfo(string Version, string Tag, string ReleaseUrl, string? ZipUrl, string? ChecksumsUrl)
{
    /// <summary>True if the release has both downloads that an in-place update needs.</summary>
    [JsonIgnore]
    public bool CanInstall => ZipUrl is not null && ChecksumsUrl is not null;

    /// <summary>File name of the ZIP in the release and in <c>SHA256SUMS.txt</c>.</summary>
    [JsonIgnore]
    public string ZipName => UpdateChecker.ZipName(Version);
}

public enum UpdateCheckOutcome
{
    /// <summary>No request was made: placeholder repository, switched off, already checked today, or a check is running.</summary>
    Skipped,

    /// <summary>The request worked and there is no newer release (also when the repository has no release or is private: 404).</summary>
    UpToDate,

    UpdateAvailable,

    /// <summary>No network, proxy, timeout, rate limit or an unreadable answer. Only logged; tried again after an hour.</summary>
    Failed,
}

public sealed record UpdateCheckResult(UpdateCheckOutcome Outcome, UpdateInfo? Update);

/// <summary>
/// Asks GitHub at most once a day whether a newer release exists (PLAN.md 9). One request:
/// <c>GET https://api.github.com/repos/&lt;Repository&gt;/releases/latest</c> with the generic user agent
/// <c>FileDetective/&lt;version&gt;</c> and nothing else about the user. As long as <see cref="Repository"/> is the
/// placeholder, no request is ever made. All errors are logged quietly and never thrown. The HTTP handler and the clock
/// are injected for tests.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>
    /// THE ONE PLACE for the GitHub account and repository (the download links in <c>README.md</c> must match). A fork
    /// that sets the owner back to the placeholder <c>OWNER</c> gets no update check, no link and no update.
    /// </summary>
    public const string Repository = "christophfetz/FileDetective";

    /// <summary>After a failed attempt the next one comes at the earliest after this time.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private const string PlaceholderOwner = "OWNER";

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly string _repository;
    private readonly AppVersion _current;
    private readonly string? _stateFile;
    private readonly object _gate = new();
    private string? _lastCheckDate;     // local calendar day of the last completed check
    private DateTimeOffset? _lastFailure;
    private UpdateInfo? _known;
    private bool _running;

    /// <param name="currentVersion">Version of the running program, e.g. <c>0.1.0</c>.</param>
    /// <param name="handler">HTTP handler; null for the real network. The checker does not dispose it.</param>
    /// <param name="clock">Clock; null for the system clock.</param>
    /// <param name="stateFile">File remembering the day of the last check and the newest release; null keeps it in memory.</param>
    /// <param name="repository">Repository <c>owner/name</c>; the default is <see cref="Repository"/>.</param>
    public UpdateChecker(
        string currentVersion,
        HttpMessageHandler? handler = null,
        TimeProvider? clock = null,
        string? stateFile = null,
        string repository = Repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        _repository = repository;
        _clock = clock ?? TimeProvider.System;
        _stateFile = stateFile;
        bool parsed = AppVersion.TryParse(currentVersion, out var version);
        _current = parsed ? version : default;
        string agentVersion = parsed ? version.ToString() : "0.0.0";

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FileDetective", agentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        LoadState();
    }

    /// <summary>The repository this checker asks.</summary>
    public string RepositoryName => _repository;

    /// <summary>True if the repository is a real one and not the placeholder.</summary>
    public bool IsConfigured => IsRealRepository(_repository);

    /// <summary>The newest known release if it is newer than the running program; from the last check, also from an earlier start.</summary>
    public UpdateInfo? Available
    {
        get
        {
            lock (_gate)
            {
                return _known;
            }
        }
    }

    /// <summary>Repository page, e.g. <c>https://github.com/owner/FileDetective</c>; null while the placeholder stands.</summary>
    public static string? RepositoryUrl => UrlOf(Repository);

    /// <summary>Releases page of the repository; null while the placeholder stands.</summary>
    public static string? ReleasesUrl => RepositoryUrl is { } url ? url + "/releases" : null;

    /// <summary>The ZIP name of a release: <c>FileDetective-&lt;version&gt;-win-x64.zip</c>.</summary>
    public static string ZipName(string version) => $"FileDetective-{version}-win-x64.zip";

    public static string? UrlOf(string repository) => IsRealRepository(repository) ? "https://github.com/" + repository : null;

    public static bool IsRealRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        string[] parts = repository.Split('/');
        return parts.Length == 2
            && parts.All(p => p.Length > 0 && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            && !parts[0].Equals(PlaceholderOwner, StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks if a check is due (switched on, real repository, not yet completed on this local calendar day, not
    /// within an hour after a failure) and then asks GitHub once. Never throws.
    /// </summary>
    public async Task<UpdateCheckResult> CheckIfDueAsync(bool enabled, CancellationToken cancellation = default)
    {
        if (!enabled || !IsConfigured)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Skipped, Available);
        }

        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (_running
                || _lastCheckDate == Today()
                || (_lastFailure is { } failed && now - failed < RetryAfterFailure))
            {
                return new UpdateCheckResult(UpdateCheckOutcome.Skipped, _known);
            }

            _running = true;
        }

        try
        {
            return await RequestAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private async Task<UpdateCheckResult> RequestAsync(CancellationToken cancellation)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
            using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // No release yet, or a private repository: nothing to offer, and no reason to ask again today.
                FileLog.Shared.Info("Update check: no release found (HTTP 404).");
                return Completed(null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"HTTP {(int)response.StatusCode}");
            }

            string json = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
            var latest = Parse(json, _repository);
            if (latest is null)
            {
                return Failed("the answer has no usable release");
            }

            bool newer = AppVersion.TryParse(latest.Version, out var v) && v > _current;
            FileLog.Shared.Info($"Update check: latest release {latest.Tag}, running {_current}{(newer ? ", update available" : string.Empty)}.");
            return Completed(newer ? latest : null);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckOutcome.Skipped, Available);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            return Failed(ex.Message);
        }
    }

    private UpdateCheckResult Completed(UpdateInfo? update)
    {
        lock (_gate)
        {
            _lastCheckDate = Today();
            _lastFailure = null;
            _known = update;
            SaveState();
        }

        return new UpdateCheckResult(update is null ? UpdateCheckOutcome.UpToDate : UpdateCheckOutcome.UpdateAvailable, update);
    }

    private UpdateCheckResult Failed(string reason)
    {
        FileLog.Shared.Info($"Update check failed ({reason}); the next try comes in an hour at the earliest.");
        lock (_gate)
        {
            _lastFailure = _clock.GetUtcNow();
        }

        return new UpdateCheckResult(UpdateCheckOutcome.Failed, Available);
    }

    /// <summary>Reads the answer of <c>releases/latest</c>; null if it has no usable tag. Assets of other repositories are ignored.</summary>
    internal static UpdateInfo? Parse(string json, string repository)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagElement)
            || tagElement.ValueKind != JsonValueKind.String
            || tagElement.GetString() is not { } tag
            || !AppVersion.TryParse(tag, out _))
        {
            return null;
        }

        string versionText = tag.TrimStart('v', 'V');
        string prefix = "https://github.com/" + repository + "/";
        string releaseUrl = prefix + "releases/tag/" + Uri.EscapeDataString(tag);
        if (root.TryGetProperty("html_url", out var html) && html.ValueKind == JsonValueKind.String
            && html.GetString() is { } htmlUrl && htmlUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            releaseUrl = htmlUrl;
        }

        string? zip = null;
        string? sums = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            string zipName = ZipName(versionText);
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                if (name is null || url is null || !url.StartsWith(prefix + "releases/download/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.Equals(zipName, StringComparison.OrdinalIgnoreCase))
                {
                    zip = url;
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sums = url;
                }
            }
        }

        return new UpdateInfo(versionText, tag, releaseUrl, zip, sums);
    }

    private string Today() => _clock.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- state file

    private sealed record State(
        [property: JsonPropertyName("lastCheckDate")] string? LastCheckDate,
        [property: JsonPropertyName("latest")] UpdateInfo? Latest);

    private void LoadState()
    {
        if (_stateFile is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_stateFile) && JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFile)) is { } state)
            {
                _lastCheckDate = state.LastCheckDate;
                // The remembered release only counts while it is newer than the running program (after an update it is not).
                if (state.Latest is { } latest && AppVersion.TryParse(latest.Version, out var v) && v > _current)
                {
                    _known = latest;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged state file only means that the next check happens earlier than necessary.
        }
    }

    private void SaveState()
    {
        if (_stateFile is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(new State(_lastCheckDate, _known)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Shared.Error("Saving the update state failed", ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
