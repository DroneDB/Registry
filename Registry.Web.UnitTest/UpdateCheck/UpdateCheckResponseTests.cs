using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Services.UpdateCheck;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The answer of the update check service is untrusted text: what is read from it, what is dropped, how much is read.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UpdateCheckResponseTests"
/// </summary>
[TestFixture]
public class UpdateCheckResponseTests
{
    private static UpdateCheckResponse Parse(string json) => UpdateCheckResponseParser.Parse(Encoding.UTF8.GetBytes(json));

    [Test]
    public void TheExampleOfTheContract_MatchesTheSchema_AndIsRead()
    {
        var json = File.ReadAllText(ContractSchema.PathOf("examples/update-check-response.example.json"));

        ContractSchema.Response.Violations(json).ShouldBeEmpty();
        var response = Parse(json);
        response.LatestVersion.ShouldBe("3.2.0");
        response.ReleaseDate.ShouldBe("2027-03-15");
        response.Releases.Select(r => r.Version).ShouldBe(["3.2.0", "3.1.0"]);
        response.Releases[1].Notes.ShouldBe("## Fixed\n- Upload of files with non-ASCII names");
        response.ReleasesTruncated.ShouldBeFalse();
        response.LicenseRequestUrl.ShouldBe("https://dronedb.app/request-license");
    }

    [Test]
    public void TheTestAnswer_MatchesTheSchema() =>
        ContractSchema.Response.Violations(UpdateCheckTestKit.Answer("3.0.0")).ShouldBeEmpty();

    [TestCase("javascript:alert(1)")]
    [TestCase("data:text/html,<script>")]
    [TestCase("ftp://example.org/x")]
    [TestCase("/relative/path")]
    [TestCase("https://exa mple.org/x")]
    public void AUrlThatIsNotAnAbsoluteWebUrl_IsDropped(string url)
    {
        var json = $$"""
            {"latestVersion":"3.0.0","changelogUrl":"{{url}}","compareUrl":"{{url}}","releases":[
              {"version":"3.0.0","date":"2027-01-01","url":"{{url}}","notes":"n"}],"releasesTruncated":false}
            """;

        var response = Parse(json);

        response.ChangelogUrl.ShouldBeNull();
        response.CompareUrl.ShouldBeNull();
        response.Releases.ShouldHaveSingleItem().Url.ShouldBeEmpty();
    }

    [Test]
    public void TheLicenseRequestUrl_IsKeptOnlyWhenItIsHttps()
    {
        Parse("""{"latestVersion":null,"licenseRequestUrl":"https://dronedb.app/request-license"}""")
            .LicenseRequestUrl.ShouldBe("https://dronedb.app/request-license");
        Parse("""{"latestVersion":null,"licenseRequestUrl":"http://dronedb.app/request-license"}""").LicenseRequestUrl.ShouldBeNull();
        Parse("""{"latestVersion":null,"licenseRequestUrl":"javascript:alert(1)"}""").LicenseRequestUrl.ShouldBeNull();
        Parse("""{"latestVersion":null}""").LicenseRequestUrl.ShouldBeNull();
    }

    [Test]
    public void AReleaseWithoutAVersionOfThreeParts_IsDropped()
    {
        var response = Parse("""
            {"latestVersion":"3.0.0","releases":[
              {"version":"3.0.0","date":"2027-01-01","url":"https://e.org/3","notes":"keep"},
              {"version":"v3.0.0","date":"2027-01-01","url":"https://e.org/v","notes":"drop"},
              {"version":"3.0","date":"2027-01-01","url":"https://e.org/2","notes":"drop"},
              {"version":null,"date":"2027-01-01","url":"https://e.org/n","notes":"drop"}]}
            """);

        response.Releases.Select(r => r.Notes).ShouldBe(["keep"]);
    }

    [Test]
    public void AtMostThirtyReleases_AreKept_AndTheCutIsSaid()
    {
        var releases = string.Join(",", Enumerable.Range(1, 40).Select(i =>
            $$"""{"version":"3.{{i}}.0","date":"2027-01-01","url":"https://e.org/{{i}}","notes":"n{{i}}"}"""));

        var response = Parse($$"""{"latestVersion":"3.40.0","releases":[{{releases}}],"releasesTruncated":false}""");

        response.Releases.Length.ShouldBe(UpdateCheckResponseParser.MaxReleases);
        response.ReleasesTruncated.ShouldBeTrue();
    }

    [Test]
    public void TheNotes_AreCappedInTotal_AndTheCutIsSaid()
    {
        var big = new string('n', UpdateCheckResponseParser.MaxNotesLength / 2 + 1);
        var releases = string.Join(",", Enumerable.Range(1, 3).Select(i =>
            $$"""{"version":"3.{{i}}.0","date":"2027-01-01","url":"https://e.org/{{i}}","notes":"{{big}}"}"""));

        var response = Parse($$"""{"latestVersion":"3.3.0","releases":[{{releases}}]}""");

        response.Releases.Length.ShouldBe(1);
        response.ReleasesTruncated.ShouldBeTrue();
    }

    [Test]
    public void ADateThatIsNotADate_IsDropped()
    {
        var response = Parse("""{"latestVersion":"3.0.0","releaseDate":"yesterday","releases":[{"version":"3.0.0","date":"31/12/2026","url":"https://e.org/3","notes":""}]}""");

        response.ReleaseDate.ShouldBeNull();
        response.Releases.Single().Date.ShouldBeEmpty();
    }

    [Test]
    public void AMalformedLatestVersion_IsKeptAsText_ForTheComparisonToRefuse()
    {
        Parse("""{"latestVersion":"soon"}""").LatestVersion.ShouldBe("soon");
        Parse("""{"latestVersion":"line\nbreak"}""").LatestVersion.ShouldBe("linebreak");
    }

    [Test]
    public void UnknownMembers_AreIgnored() =>
        Parse("""{"latestVersion":"3.0.0","somethingNew":{"a":1}}""").LatestVersion.ShouldBe("3.0.0");

    // ---- how much is read

    private static UpdateCheckClient ClientFor(HttpContent content) => new(
        UpdateCheckTestKit.Factory(new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }))),
        UpdateCheckTestKit.Configure(), TimeProvider.System);

    private static Task<UpdateCheckResponse> Post(UpdateCheckClient client) =>
        client.PostAsync(new UpdateCheckPayload { InstanceId = "x", Version = "1.0.0" }, CancellationToken.None);

    /// <summary>A body of unknown length that produces <c>size</c> bytes of text.</summary>
    private sealed class EndlessStream(long size) : Stream
    {
        private long _sent;

        public long Sent => _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, size - _sent);
            Array.Fill(buffer, (byte)' ', offset, n);
            _sent += n;
            return n;
        }
    }

    [Test]
    public async Task AnAnswerOfUnknownLength_IsReadOnlyUpToTheCap()
    {
        var body = new EndlessStream(long.MaxValue / 2);

        await Should.ThrowAsync<UpdateCheckException>(() => Post(ClientFor(new StreamContent(body))));

        body.Sent.ShouldBeLessThanOrEqualTo(UpdateCheckClient.MaxResponseBytes + 64 * 1024);
    }

    [Test]
    public async Task AnAnswerJustUnderTheCap_IsRead()
    {
        var padding = new string(' ', UpdateCheckClient.MaxResponseBytes - UpdateCheckTestKit.Answer("3.0.0").Length - 1);

        var response = await Post(ClientFor(new StringContent(UpdateCheckTestKit.Answer("3.0.0") + padding, Encoding.UTF8, "application/json")));

        response.LatestVersion.ShouldBe("3.0.0");
    }
}
