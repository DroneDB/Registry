#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Storage;
using Registry.Web.Services.UpdateCheck;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The update check run: the request it posts, what it keeps, and how it fails (never as an error for the deployment).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UpdateCheckServiceTests"
/// </summary>
[TestFixture]
public class UpdateCheckServiceTests
{
    private static string Newer => "99.0.0";

    private static UpdateCheckHarness Harness(StubHandler handler, UpdateCheckOptions? options = null, HttpHostOptions? http = null) =>
        new(handler, options, http);

    private static StubHandler Answering(string body) => new(HttpStatusCode.OK, body);

    [Test]
    public async Task Check_PostsTheJsonPayload_ToTheEndpoint()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        var request = harness.Handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.ToString().ShouldBe(UpdateCheckOptions.DefaultEndpoint);
        request.ContentType.ShouldNotBeNull().ShouldStartWith("application/json");
        request.ContentType.ShouldContain("utf-8", Case.Insensitive);

        var root = JsonDocument.Parse(request.Body).RootElement;
        foreach (var field in new[] { "schemaVersion", "instanceId", "installedAt", "version", "ddbVersion", "os", "osCaption", "arch",
                     "runtime", "env", "instanceType", "db", "baseUrl", "hostname", "domain", "licenseRef", "useDeclaration" })
            root.TryGetProperty(field, out _).ShouldBeTrue(field);
        root.GetProperty("instanceId").GetString().ShouldBe(Registry.Web.Test.Support.FixedInstanceIdentity.DefaultId);
        root.GetProperty("version").GetString().ShouldBe(RegistryVersion.Current);
        ContractSchema.Payload.Violations(request.Body).ShouldBeEmpty();
    }

    [Test]
    public async Task Check_UsesTheConfiguredEndpoint()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)),
            new UpdateCheckOptions { UpdateCheckEndpoint = "https://staging.internal:8443/check" });

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Handler.Requests.ShouldHaveSingleItem().Uri.ToString().ShouldBe("https://staging.internal:8443/check");
    }

    [Test]
    public async Task Check_RefusesAnEndpointThatIsNotHttps_WithoutSendingAnything()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)),
            new UpdateCheckOptions { UpdateCheckEndpoint = "http://dronedb.app/update-check" });

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Handler.Count.ShouldBe(0);
        harness.Cache.Read().LastFailureAt.ShouldNotBeNull();
    }

    [Test]
    public async Task EveryRun_Posts_WhateverTheLastResultWas()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Handler.Count.ShouldBe(3);
    }

    [Test]
    public async Task EveryRun_Posts_AlsoAfterAFailure_AndWhileTheLastFailureIsRecent()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) => Task.FromResult(++calls % 2 == 1
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UpdateCheckTestKit.Answer(Newer), Encoding.UTF8, "application/json") }));
        using var harness = Harness(handler);

        for (var run = 1; run <= 4; run++)
        {
            await harness.Service.CheckForUpdatesAsync(CancellationToken.None);
            harness.Handler.Count.ShouldBe(run, $"run {run} must send a request whatever the previous one gave");
        }

        harness.Cache.Read().LastResult.ShouldNotBeNull();
    }

    [TestCase("disabled")]
    [TestCase("off")]
    [TestCase("none")]
    public async Task WhenDisabled_NothingIsSent_Remembered_OrNotified(string cron)
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)), new UpdateCheckOptions { UpdateCheckCron = cron });

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Handler.Count.ShouldBe(0);
        harness.Notifier.Calls.ShouldBeEmpty();
        harness.Cache.Read().ShouldBe(UpdateCheckCache.Empty);
        System.IO.File.Exists(harness.Storage.File(MagicStrings.UpdateCheckCacheFileName)).ShouldBeFalse();
    }

    [Test]
    public async Task ASuccess_IsRemembered_WithItsTime_AndTheReleases()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer, "## Highlights\n- Big news")));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        var cache = harness.Cache.Read();
        var result = cache.LastResult.ShouldNotBeNull();
        result.CheckedAt.ShouldBe(harness.Time.Now);
        result.Response.LatestVersion.ShouldBe(Newer);
        result.Response.Releases.ShouldHaveSingleItem().Notes.ShouldBe("## Highlights\n- Big news");
        cache.LastFailureAt.ShouldBeNull();
    }

    [Test]
    public async Task TheRememberedResult_SurvivesARestart()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)));
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        var restarted = new UpdateCheckCacheStore(harness.Storage.Storage, UpdateCheckTestKit.Disk,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateCheckCacheStore>.Instance);

        restarted.Read().LastResult.ShouldNotBeNull().Response.LatestVersion.ShouldBe(Newer);
    }

    // ---- failures

    private delegate Task<HttpResponseMessage> Responder(HttpRequestMessage request, CancellationToken ct);

    private static Responder Status(HttpStatusCode status, string body = "", string mediaType = "application/json") =>
        (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    private static Responder Redirect(HttpStatusCode status) => (_, _) =>
    {
        var response = new HttpResponseMessage(status);
        response.Headers.Location = new Uri("https://elsewhere.example.org/update-check");
        return Task.FromResult(response);
    };

    private static StubHandler Redirecting(HttpStatusCode status)
    {
        var redirect = Redirect(status);
        return new StubHandler((request, ct) => redirect(request, ct));
    }

    public static System.Collections.Generic.IEnumerable<TestCaseData> Failures()
    {
        yield return new TestCaseData(Redirect(HttpStatusCode.MovedPermanently)).SetName("a 301 redirect");
        yield return new TestCaseData(Redirect(HttpStatusCode.TemporaryRedirect)).SetName("a 307 redirect");
        yield return new TestCaseData(Status(HttpStatusCode.BadRequest, "{}")).SetName("a 400");
        yield return new TestCaseData(Status(HttpStatusCode.NotFound, "{}")).SetName("a 404");
        yield return new TestCaseData(Status(HttpStatusCode.TooManyRequests, "{}")).SetName("a 429");
        yield return new TestCaseData(Status(HttpStatusCode.InternalServerError, "{}")).SetName("a 500");
        yield return new TestCaseData(Status(HttpStatusCode.BadGateway, "<html>")).SetName("a 502");
        yield return new TestCaseData((Responder)((_, _) => throw new HttpRequestException("no route"))).SetName("an unreachable host");
        yield return new TestCaseData((Responder)((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(new BrokenStream()) }))).SetName("a connection that breaks while the answer is read");
        yield return new TestCaseData(Status(HttpStatusCode.OK, "<html>maintenance</html>", "text/html")).SetName("a page that is not JSON");
        yield return new TestCaseData(Status(HttpStatusCode.OK, "{\"latestVersion\": ")).SetName("truncated JSON");
        yield return new TestCaseData(Status(HttpStatusCode.OK, "[]")).SetName("a JSON array");
        yield return new TestCaseData(Status(HttpStatusCode.OK, "\"3.0.0\"")).SetName("a JSON string");
        yield return new TestCaseData(Status(HttpStatusCode.OK, "{}")).SetName("an object without latestVersion");
        yield return new TestCaseData(Status(HttpStatusCode.OK, "{\"latestVersion\":\"3.0.0\",\"releases\":\"none\"}")).SetName("releases of the wrong type");
        yield return new TestCaseData(Status(HttpStatusCode.OK, "{\"latestVersion\":\"3.0.0\",\"releasesTruncated\":{}}")).SetName("a flag of the wrong type");
        yield return new TestCaseData(Status(HttpStatusCode.OK, "")).SetName("an empty body");
        yield return new TestCaseData(Status(HttpStatusCode.NoContent, "")).SetName("no content");
        yield return new TestCaseData(Status(HttpStatusCode.OK, new string(' ', UpdateCheckClient.MaxResponseBytes) + UpdateCheckTestKit.Answer("9.0.0"))).SetName("an answer over 1 MB");
    }

    [TestCaseSource(nameof(Failures))]
    public async Task AFailure_KeepsThePreviousResult_RecordsWhen_AndNeverThrows(object failure)
    {
        var failing = (Responder)failure;
        var good = Status(HttpStatusCode.OK, UpdateCheckTestKit.Answer("3.0.0"));
        var down = false;
        using var harness = Harness(new StubHandler((request, ct) => (down ? failing : good)(request, ct)));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);
        var previous = harness.Cache.Read().LastResult.ShouldNotBeNull();
        harness.Time.Advance(TimeSpan.FromDays(1));
        down = true;

        await Should.NotThrowAsync(() => harness.Service.CheckForUpdatesAsync(CancellationToken.None));

        var cache = harness.Cache.Read();
        JsonSerializer.Serialize(cache.LastResult).ShouldBe(JsonSerializer.Serialize(previous), "the previous good result is kept");
        cache.LastFailureAt.ShouldBe(harness.Time.Now);
        harness.Notifier.Calls.Count.ShouldBe(1, "only the first, successful, check is notified");
    }

    [TestCaseSource(nameof(Failures))]
    public async Task AFailure_IsLoggedAtDebugLevel_Only(object failure)
    {
        var failing = (Responder)failure;
        using var harness = Harness(new StubHandler((request, ct) => failing(request, ct)));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Logs.Entries.ShouldNotBeEmpty();
        harness.Logs.Entries.ShouldAllBe(entry => entry.Level == LogLevel.Debug);
        harness.Notifier.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ARedirect_IsNotFollowed()
    {
        var target = new StubHandler(HttpStatusCode.OK, UpdateCheckTestKit.Answer(Newer));
        using var harness = Harness(Redirecting(HttpStatusCode.Found));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Handler.Count.ShouldBe(1);
        target.Count.ShouldBe(0);
        harness.Cache.Read().LastResult.ShouldBeNull();
    }

    [Test]
    public async Task ATimeout_IsAFailure_AfterTenSeconds()
    {
        var hang = new StubHandler(async (_, ct) =>
        {
            await Stall.UntilCanceledAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var harness = Harness(hang);

        var armed = harness.Time.WhenTimerArmed(TimeSpan.FromSeconds(10));
        var check = harness.Service.CheckForUpdatesAsync(CancellationToken.None);
        await armed;
        check.IsCompleted.ShouldBeFalse();
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await check;

        harness.Cache.Read().LastFailureAt.ShouldNotBeNull();
        harness.Notifier.Calls.ShouldBeEmpty();
        harness.Logs.Entries.ShouldAllBe(entry => entry.Level == LogLevel.Debug);
    }

    [Test]
    public async Task AnAnswerThatStallsWhileItIsRead_IsAFailureToo()
    {
        var stalling = new StubHandler((_, ct) =>
        {
            var content = new StreamContent(new StallingStream());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var harness = Harness(stalling);

        var armed = harness.Time.WhenTimerArmed(TimeSpan.FromSeconds(10));
        var check = harness.Service.CheckForUpdatesAsync(CancellationToken.None);
        await armed;
        harness.Time.Advance(TimeSpan.FromSeconds(10));
        await check;

        harness.Cache.Read().LastFailureAt.ShouldNotBeNull();
    }

    [Test]
    public async Task ShuttingDown_CancelsTheCheck_AndIsNotAFailure()
    {
        var hang = new StubHandler(async (_, ct) =>
        {
            await Stall.UntilCanceledAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var harness = Harness(hang);
        using var shutdown = new CancellationTokenSource();

        var armed = harness.Time.WhenTimerArmed(TimeSpan.FromSeconds(10));
        var check = harness.Service.CheckForUpdatesAsync(shutdown.Token);
        await armed;
        await shutdown.CancelAsync();

        var canceled = false;
        try
        {
            await check;
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }

        canceled.ShouldBeTrue();
        harness.Cache.Read().LastFailureAt.ShouldBeNull();
    }

    [Test]
    public async Task AFailureAfterASuccess_DoesNotChangeWhatTheHubShows()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UpdateCheckTestKit.Answer(Newer), Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        using var harness = Harness(handler);
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);
        var before = harness.Service.GetUpdateInfo();

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Service.GetUpdateInfo().ShouldBe(before, new UpdateInfoComparer());
    }

    // ---- the comparison of versions

    private static string Relative(string latest)
    {
        var installed = Version.Parse(RegistryVersion.Current!);
        return latest switch
        {
            "older" => "0.0.1",
            "equal" => installed.ToString(),
            "newer-patch" => new Version(installed.Major, installed.Minor, installed.Build + 1).ToString(),
            "newer-minor" => new Version(installed.Major, installed.Minor + 1, 0).ToString(),
            "newer-major" => new Version(installed.Major + 1, 0, 0).ToString(),
            _ => latest
        };
    }

    [TestCase("older", false)]
    [TestCase("equal", false)]
    [TestCase("newer-patch", true)]
    [TestCase("newer-minor", true)]
    [TestCase("newer-major", true)]
    [TestCase("not-a-version", false)]
    [TestCase("3", false)]
    [TestCase("", false)]
    [TestCase("1.2.3-beta", false)]
    public async Task IsUpdateAvailable_ComparesWithTheInstalledVersion(string latest, bool expected)
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Relative(latest), version: "99.9.9")));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Cache.Read().LastResult.ShouldNotBeNull();
        harness.Service.GetUpdateInfo().IsUpdateAvailable.ShouldBe(expected);
    }

    [Test]
    public async Task ANullLatestVersion_IsNoUpdate()
    {
        using var harness = Harness(Answering("""{"latestVersion":null,"releaseDate":null,"changelogUrl":null,"compareUrl":null,"releases":[],"releasesTruncated":false,"licenseRequestUrl":null}"""));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        var info = harness.Service.GetUpdateInfo();
        info.IsUpdateAvailable.ShouldBeFalse();
        info.LatestVersion.ShouldBeNull();
        info.LastChecked.ShouldNotBeNull();
        info.LicenseRequestUrl.ShouldStartWith(UpdateInfoBuilder.DefaultLicenseRequestUrl);
    }

    // ---- the licence request link

    [Test]
    public async Task TheLicenceLink_UsesTheFormTheServiceNames_WhenItIsHttps()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer, licenseRequestUrl: "https://dronedb.app/de/request-license")),
            http: new HttpHostOptions { ExternalUrlOverride = "https://hub.example.org" });
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        var link = harness.Service.GetUpdateInfo().LicenseRequestUrl;

        link.ShouldBe($"https://dronedb.app/de/request-license?instance={Registry.Web.Test.Support.FixedInstanceIdentity.DefaultId}" +
                      $"&url={Uri.EscapeDataString("https://hub.example.org")}&version={Uri.EscapeDataString(RegistryVersion.Current!)}");
    }

    [TestCase("http://dronedb.app/request-license")]
    [TestCase("javascript:alert(1)")]
    [TestCase("/request-license")]
    [TestCase("")]
    public async Task TheLicenceLink_FallsBackToTheBuiltInForm_WhenTheServiceNamesNoHttpsOne(string licenseRequestUrl)
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer, licenseRequestUrl: licenseRequestUrl)));
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Service.GetUpdateInfo().LicenseRequestUrl.ShouldStartWith("https://dronedb.app/request-license?instance=");
    }

    [Test]
    public async Task TheLicenceLink_HasEmptyUrl_WhenTheDeploymentHasNoPublicUrl()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)));
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Service.GetUpdateInfo().LicenseRequestUrl.ShouldContain("&url=&version=");
    }

    [Test]
    public async Task TheNotes_AreOnlyThoseNewerThanTheInstalledVersion()
    {
        var installed = Version.Parse(RegistryVersion.Current!);
        var older = new Version(installed.Major, installed.Minor, installed.Build).ToString();
        var newer = new Version(installed.Major + 1, 0, 0).ToString();
        var answer = $$"""
            {"latestVersion":"{{newer}}","releaseDate":"2027-01-01","changelogUrl":null,"compareUrl":"https://github.com/DroneDB/Registry/compare/a...b",
             "releases":[
               {"version":"{{newer}}","date":"2027-01-01","url":"https://e.org/n","notes":"## Fixed\n- New"},
               {"version":"{{older}}","date":"2026-01-01","url":"https://e.org/o","notes":"## Fixed\n- Already installed"}],
             "releasesTruncated":false,"licenseRequestUrl":null}
            """;
        using var harness = Harness(Answering(answer));
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        var info = harness.Service.GetUpdateInfo();

        info.ReleaseNotes.ShouldNotBeNull().ShouldContain("- New");
        info.ReleaseNotes.ShouldNotContain("Already installed");
    }

    [Test]
    public async Task WhenNoUpdateIsAvailable_ThereAreNoNotes_AndNoSecurityFlag()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer("0.0.1", "## Security\n- Old hole")));
        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        var info = harness.Service.GetUpdateInfo();

        info.IsUpdateAvailable.ShouldBeFalse();
        info.IsSecurityUpdate.ShouldBeFalse();
        info.ReleaseNotes.ShouldBeEmpty();
    }

    // ---- the notifier

    [Test]
    public async Task TheNotifier_IsCalledAfterASuccess_AndSeesTheRememberedResult()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer, "## Security\n- Fixed a hole")));
        harness.Notifier.OnNotify = _ =>
        {
            harness.Cache.Read().LastResult.ShouldNotBeNull();
            return Task.CompletedTask;
        };

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        var update = harness.Notifier.Calls.ShouldHaveSingleItem();
        update.LatestVersion.ShouldBe(Newer);
        update.IsUpdateAvailable.ShouldBeTrue();
        update.IsSecurityUpdate.ShouldBeTrue();
        update.CurrentVersion.ShouldBe(RegistryVersion.Current);
    }

    [Test]
    public async Task TheNotifier_IsNotCalledAfterAFailure()
    {
        using var harness = Harness(new StubHandler(HttpStatusCode.InternalServerError));

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Notifier.Calls.ShouldBeEmpty();
    }

    /// <summary>What a notifier meets on a deployment: the database that holds the administrators, the storage folder.</summary>
    public static System.Collections.Generic.IEnumerable<TestCaseData> NotifierFailures()
    {
        yield return new TestCaseData(new StubDbException()).SetName("the identity database is down");
        yield return new TestCaseData(new StorageDocumentException("Cannot store the cache", new System.IO.IOException("disk full")))
            .SetName("the storage folder refuses the record");
    }

    [TestCaseSource(nameof(NotifierFailures))]
    public async Task ANotifierThatFails_DoesNotFailTheCheck_NorLoseTheResult(Exception failure)
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)));
        harness.Notifier.Failure = failure;

        await Should.NotThrowAsync(() => harness.Service.CheckForUpdatesAsync(CancellationToken.None));

        harness.Cache.Read().LastResult.ShouldNotBeNull();
        harness.Cache.Read().LastFailureAt.ShouldBeNull();
        harness.Logs.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning);
    }

    [Test]
    public async Task ABugInTheNotifier_IsNotSwallowed()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)));
        harness.Notifier.Failure = new InvalidOperationException("a bug");

        await Should.ThrowAsync<InvalidOperationException>(() => harness.Service.CheckForUpdatesAsync(CancellationToken.None));

        harness.Cache.Read().LastResult.ShouldNotBeNull("the result was remembered before the notifier ran");
    }

    private sealed class StubDbException : System.Data.Common.DbException
    {
        public StubDbException() : base("database down")
        {
        }
    }

    [Test]
    public async Task ThePreviousNotification_SurvivesANewResult()
    {
        using var harness = Harness(Answering(UpdateCheckTestKit.Answer(Newer)));
        harness.Cache.Update(cache => cache with { LastNotifiedVersion = "98.0.0" });

        await harness.Service.CheckForUpdatesAsync(CancellationToken.None);

        harness.Cache.Read().LastNotifiedVersion.ShouldBe("98.0.0");
    }

    private sealed class UpdateInfoComparer : System.Collections.Generic.IEqualityComparer<Registry.Web.Models.DTO.UpdateInfoDto>
    {
        public bool Equals(Registry.Web.Models.DTO.UpdateInfoDto? x, Registry.Web.Models.DTO.UpdateInfoDto? y) =>
            JsonSerializer.Serialize(x) == JsonSerializer.Serialize(y);

        public int GetHashCode(Registry.Web.Models.DTO.UpdateInfoDto obj) => JsonSerializer.Serialize(obj).GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>A body whose connection breaks as soon as it is read.</summary>
    private sealed class BrokenStream : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new System.IO.IOException("connection reset");
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new System.IO.IOException("connection reset");
    }

    /// <summary>A body that sends nothing until the exchange is canceled.</summary>
    private sealed class StallingStream : System.IO.Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, System.IO.SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Stall.UntilCanceledAsync(cancellationToken);
            return 0;
        }
    }
}
