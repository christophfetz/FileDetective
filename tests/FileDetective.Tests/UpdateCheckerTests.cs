using System.Net;
using System.Text;
using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>A handler that answers from a function and records every request.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] data)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
}

public class UpdateCheckerTests : IDisposable
{
    private const string Repo = "someone/FileDetective";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fd-update-" + Guid.NewGuid().ToString("N"));

    public UpdateCheckerTests() => Directory.CreateDirectory(_folder);

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

    internal static string ReleaseJson(string tag, string repo = Repo, bool withAssets = true)
    {
        string version = tag.TrimStart('v');
        string assets = withAssets
            ? $$"""
              [
                {"name":"FileDetective-{{version}}-win-x64.zip","browser_download_url":"https://github.com/{{repo}}/releases/download/{{tag}}/FileDetective-{{version}}-win-x64.zip"},
                {"name":"FileDetective-{{version}}-win-x64-setup.exe","browser_download_url":"https://github.com/{{repo}}/releases/download/{{tag}}/FileDetective-{{version}}-win-x64-setup.exe"},
                {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/{{repo}}/releases/download/{{tag}}/SHA256SUMS.txt"}
              ]
              """
            : "[]";
        return $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/{{repo}}/releases/tag/{{tag}}","assets":{{assets}}}""";
    }

    private static ManualTimeProvider Clock(int day = 14, int hour = 9)
        => new(new DateTimeOffset(2026, 11, day, hour, 0, 0, TimeSpan.Zero), ManualTimeProvider.UtcPlusOne);

    [Fact]
    public void ConfiguredRepository_IsReal()
    {
        Assert.Equal("christophfetz/FileDetective", UpdateChecker.Repository);
        Assert.True(UpdateChecker.IsRealRepository(UpdateChecker.Repository));
        Assert.Equal("https://github.com/christophfetz/FileDetective", UpdateChecker.RepositoryUrl);
        Assert.Equal("https://github.com/christophfetz/FileDetective/releases", UpdateChecker.ReleasesUrl);
    }

    [Fact]
    public async Task PlaceholderRepository_MakesNoRequest()
    {
        const string placeholder = "OWNER/FileDetective";
        Assert.False(UpdateChecker.IsRealRepository(placeholder));
        Assert.Null(UpdateChecker.UrlOf(placeholder));

        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson("v9.9.9")));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: placeholder);
        var result = await checker.CheckIfDueAsync(enabled: true);

        Assert.Equal(UpdateCheckOutcome.Skipped, result.Outcome);
        Assert.Empty(handler.Requests);
        Assert.False(checker.IsConfigured);
    }

    [Fact]
    public async Task SwitchedOff_MakesNoRequest()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson("v9.9.9")));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: Repo);
        var result = await checker.CheckIfDueAsync(enabled: false);

        Assert.Equal(UpdateCheckOutcome.Skipped, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Request_UsesTheReleasesLatestUrlAndAGenericUserAgent()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson("v0.2.0")));
        using var checker = new UpdateChecker("0.1.0+abc123", handler, Clock(), repository: Repo);
        await checker.CheckIfDueAsync(enabled: true);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"https://api.github.com/repos/{Repo}/releases/latest", request.RequestUri!.ToString());
        Assert.Equal("FileDetective/0.1.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Null(request.Content);
    }

    [Fact]
    public async Task NewerRelease_IsReportedWithDownloads()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson("v0.2.0")));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: Repo);
        var result = await checker.CheckIfDueAsync(enabled: true);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        var info = result.Update!;
        Assert.Equal("0.2.0", info.Version);
        Assert.Equal("FileDetective-0.2.0-win-x64.zip", info.ZipName);
        Assert.True(info.CanInstall);
        Assert.EndsWith("/v0.2.0/FileDetective-0.2.0-win-x64.zip", info.ZipUrl);
        Assert.EndsWith("/v0.2.0/SHA256SUMS.txt", info.ChecksumsUrl);
        Assert.Same(info, checker.Available);
    }

    [Theory]
    [InlineData("0.1.0", "v0.1.0", false)]   // same version
    [InlineData("0.2.0", "v0.1.0", false)]   // running is newer
    [InlineData("0.1.0", "v0.1.1", true)]
    [InlineData("0.9.0", "v0.10.0", true)]   // numbers, not text
    [InlineData("0.1.0", "v1.0.0", true)]
    [InlineData("0.2.0-rc.1", "v0.2.0", true)] // the release is newer than its pre-release
    [InlineData("0.2.0", "v0.2.0-rc.1", false)]
    public async Task VersionComparison_DecidesWhetherAnUpdateIsOffered(string running, string tag, bool offered)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson(tag)));
        using var checker = new UpdateChecker(running, handler, Clock(), repository: Repo);
        var result = await checker.CheckIfDueAsync(enabled: true);

        Assert.Equal(offered ? UpdateCheckOutcome.UpdateAvailable : UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Equal(offered, checker.Available is not null);
    }

    [Fact]
    public void AppVersion_ParsesAndOrders()
    {
        Assert.True(AppVersion.TryParse("v1.2.3+build.5", out var v));
        Assert.Equal((1, 2, 3, string.Empty), (v.Major, v.Minor, v.Patch, v.PreRelease));
        Assert.True(AppVersion.TryParse("2", out var two));
        Assert.Equal("2.0.0", two.ToString());
        Assert.False(AppVersion.TryParse("", out _));
        Assert.False(AppVersion.TryParse("1.x.3", out _));
        Assert.False(AppVersion.TryParse("1.2.3.4", out _));
        Assert.False(AppVersion.TryParse("1.2.3-", out _));

        string[] ascending = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0"];
        for (int i = 0; i + 1 < ascending.Length; i++)
        {
            Assert.True(AppVersion.TryParse(ascending[i], out var a));
            Assert.True(AppVersion.TryParse(ascending[i + 1], out var b));
            Assert.True(a < b, $"{ascending[i]} < {ascending[i + 1]}");
        }
    }

    [Fact]
    public async Task OnlyOneRequestPerCalendarDay()
    {
        var clock = Clock(day: 14, hour: 9); // local time is UTC+1: 10:00
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson("v0.1.0")));
        using var checker = new UpdateChecker("0.1.0", handler, clock, repository: Repo);

        Assert.Equal(UpdateCheckOutcome.UpToDate, (await checker.CheckIfDueAsync(true)).Outcome);
        clock.Advance(TimeSpan.FromHours(5)); // 15:00 the same day
        Assert.Equal(UpdateCheckOutcome.Skipped, (await checker.CheckIfDueAsync(true)).Outcome);
        clock.Advance(TimeSpan.FromHours(8)); // 23:00 local: still the same day
        Assert.Equal(UpdateCheckOutcome.Skipped, (await checker.CheckIfDueAsync(true)).Outcome);
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromHours(2)); // 01:00 the next day
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await checker.CheckIfDueAsync(true)).Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TheDayIsRemembered_AcrossStarts()
    {
        string state = Path.Combine(_folder, "update-state.json");
        var clock = Clock();
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson("v0.2.0")));
        using (var first = new UpdateChecker("0.1.0", handler, clock, state, Repo))
        {
            Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await first.CheckIfDueAsync(true)).Outcome);
        }

        // A new start on the same day asks nothing, but still knows about the newer release.
        using var second = new UpdateChecker("0.1.0", handler, clock, state, Repo);
        Assert.Equal("0.2.0", second.Available?.Version);
        Assert.Equal(UpdateCheckOutcome.Skipped, (await second.CheckIfDueAsync(true)).Outcome);
        Assert.Single(handler.Requests);

        // After the update the remembered release is no longer newer.
        using var updated = new UpdateChecker("0.2.0", handler, clock, state, Repo);
        Assert.Null(updated.Available);
    }

    [Fact]
    public async Task NotFound_IsQuietAndCountsAsChecked()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: Repo);

        var result = await checker.CheckIfDueAsync(true); // private repository or no release yet
        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Update);
        Assert.Equal(UpdateCheckOutcome.Skipped, (await checker.CheckIfDueAsync(true)).Outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NetworkError_IsQuiet_AndRetriedAfterAnHour()
    {
        var clock = Clock();
        int calls = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("No such host is known.");
            }

            return FakeHttpHandler.Json(ReleaseJson("v0.2.0"));
        });
        using var checker = new UpdateChecker("0.1.0", handler, clock, repository: Repo);

        var failed = await checker.CheckIfDueAsync(true); // must not throw
        Assert.Equal(UpdateCheckOutcome.Failed, failed.Outcome);
        Assert.Null(checker.Available);

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(UpdateCheckOutcome.Skipped, (await checker.CheckIfDueAsync(true)).Outcome);
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await checker.CheckIfDueAsync(true)).Outcome);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]          // rate limit
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherHttpErrors_AreQuiet(HttpStatusCode status)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}", status));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: Repo);
        Assert.Equal(UpdateCheckOutcome.Failed, (await checker.CheckIfDueAsync(true)).Outcome);
    }

    [Fact]
    public async Task UnreadableAnswer_IsQuiet()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("this is not json"));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: Repo);
        Assert.Equal(UpdateCheckOutcome.Failed, (await checker.CheckIfDueAsync(true)).Outcome);

        var noTag = new FakeHttpHandler(_ => FakeHttpHandler.Json("{\"name\":\"x\"}"));
        using var checker2 = new UpdateChecker("0.1.0", noTag, Clock(), repository: Repo);
        Assert.Equal(UpdateCheckOutcome.Failed, (await checker2.CheckIfDueAsync(true)).Outcome);
    }

    [Fact]
    public async Task AssetsOfAnotherRepository_AreIgnored()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson("v0.2.0", repo: "evil/other")));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: Repo);
        var result = await checker.CheckIfDueAsync(true);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.False(result.Update!.CanInstall);
        Assert.Null(result.Update.ZipUrl);
        Assert.StartsWith($"https://github.com/{Repo}/", result.Update.ReleaseUrl); // the link stays inside the repository
    }

    [Fact]
    public async Task ReleaseWithoutAssets_CannotBeInstalledInPlace()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReleaseJson("v0.2.0", withAssets: false)));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: Repo);
        var result = await checker.CheckIfDueAsync(true);
        Assert.False(result.Update!.CanInstall);
    }

    [Fact]
    public async Task ConcurrentCalls_MakeOneRequest()
    {
        var gate = new TaskCompletionSource();
        var handler = new SlowHandler(gate.Task, ReleaseJson("v0.1.0"));
        using var checker = new UpdateChecker("0.1.0", handler, Clock(), repository: Repo);

        var first = checker.CheckIfDueAsync(true);
        var second = await checker.CheckIfDueAsync(true);
        Assert.Equal(UpdateCheckOutcome.Skipped, second.Outcome);
        gate.SetResult();
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await first).Outcome);
        Assert.Equal(1, handler.Count);
    }

    private sealed class SlowHandler(Task release, string json) : HttpMessageHandler
    {
        public int Count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            await release;
            return FakeHttpHandler.Json(json);
        }
    }
}
