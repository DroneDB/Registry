using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Middlewares;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Who may frame what. A viewer page (or a saved view) opened with <c>?embed=1</c> answers
/// <c>Content-Security-Policy: frame-ancestors *</c> and no <c>X-Frame-Options</c>, so another site can embed it.
/// Every other page, and the API, answers <c>X-Frame-Options: SAMEORIGIN</c> with or without <c>embed=1</c>, so a site
/// cannot frame the sign-in, account, upload, administration or report pages of a signed-in user. The path is matched
/// as it was requested, percent-encoded: the encoded path of a file holds <c>%2F</c>, <c>%2B</c> and <c>%3D</c>.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~EmbedFramingTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class EmbedFramingTests
{
    // An encoded file path as the Hub writes it into a viewer URL
    private const string EncodedPath = "6ZW%2F5Z%2BOLnRpZg%3D%3D";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<(string? FrameOptions, string? Csp)> FramingHeadersAsync(string url)
    {
        using var response = await _client.GetAsync(url);
        string? Single(string name) => response.Headers.TryGetValues(name, out var values) ? values.Single() : null;
        return (Single("X-Frame-Options"), Single("Content-Security-Policy"));
    }

    [TestCase("/r/acme/site/view/" + EncodedPath + "/map")]
    [TestCase("/r/acme/site/view/" + EncodedPath + "/pointcloud/")]
    [TestCase("/r/acme/site/view/abc/unified")]
    [TestCase("/r/acme/site/views/9f3a")]
    public async Task AViewerPageWithEmbed_CanBeFramedByAnySite(string path)
    {
        var (frameOptions, csp) = await FramingHeadersAsync(path + "?embed=1");

        frameOptions.ShouldBeNull();
        csp.ShouldBe("frame-ancestors *");
    }

    [TestCase("/r/acme/site/view/" + EncodedPath + "/map")]
    [TestCase("/r/acme/site/views/9f3a")]
    public async Task AViewerPageWithoutEmbed_StaysSameOrigin(string path)
    {
        var (frameOptions, csp) = await FramingHeadersAsync(path);

        frameOptions.ShouldBe("SAMEORIGIN");
        csp.ShouldBeNull();
    }

    [TestCase("/login")]
    [TestCase("/account")]
    [TestCase("/upload/abc")]
    [TestCase("/admin/users")]
    [TestCase("/r/acme/site/reports/9f3a")]
    [TestCase("/r/acme/site/view/abc/map/extra")]
    [TestCase("/r/acme/site/view/abc/other")]
    [TestCase("/r/acme/site")]
    [TestCase("/users/authenticate")]
    [TestCase("/orgs/acme/ds/site/entries")]
    public async Task AnyOtherPageOrTheApi_StaysSameOriginEvenWithEmbed(string path)
    {
        var (frameOptions, csp) = await FramingHeadersAsync(path + "?embed=1");

        frameOptions.ShouldBe("SAMEORIGIN");
        csp.ShouldBeNull();
    }

    [Test]
    public async Task EmbedHasToBeOne()
    {
        var (frameOptions, csp) = await FramingHeadersAsync("/r/acme/site/views/9f3a?embed=0");

        frameOptions.ShouldBe("SAMEORIGIN");
        csp.ShouldBeNull();
    }

    [TestCase("/r/acme/site/view/" + EncodedPath + "/map", true)]
    [TestCase("/r/acme/site/view/a%2Fb/splat", true)]
    [TestCase("/r/acme/site/view/a/b/map", false)] // an unencoded "/" splits the segment
    [TestCase("/r/acme/site/view/abc/panorama/", true)]
    [TestCase("/r/acme/site/view/abc/markdown", true)]
    [TestCase("/r/acme/site/view/abc/model", true)]
    [TestCase("/r/acme/site/views/", false)]
    [TestCase("/r//site/views/x", false)]
    [TestCase("/x/r/acme/site/views/x", false)]
    [TestCase("/r/acme/site/views/x/y", false)]
    public void ThePathIsMatchedAsRequested(string rawPath, bool embeddable) =>
        SecurityHeadersMiddleware.IsEmbeddablePath(rawPath).ShouldBe(embeddable);
}
