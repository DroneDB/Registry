#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters.Thumbnail;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Services.Adapters;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-01 item 5: the ThumbnailGenerator host (<c>POST /generate-thumbnail</c>) read any file path a
/// caller sent, with no authentication. Now:
/// <list type="bullet">
/// <item>a shared token (<c>AppSettings:ThumbnailGeneratorToken</c>, header <c>X-Thumbnail-Token</c>) is
/// required: missing/wrong token -> 401; an empty configured token refuses to build the handler;</item>
/// <item>the path must lie under the datasets or temp root (<c>CommonUtils.IsUnder</c>, also used by
/// <c>ValidateRelativePath</c>): outside, <c>..</c> traversal and prefix siblings -> 403;</item>
/// <item>the web-side client (<c>RemoteThumbnailGenerator</c>) sends the token.</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq01ThumbnailHostTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq01ThumbnailHostTests
{
    private const string Token = "rq01-thumbnail-token-0123456789";
    private const string MimeType = "image/webp";
    private static readonly byte[] ThumbnailBytes = [1, 2, 3, 4];

    private string _root = null!;
    private string _datasets = null!;
    private string _temp = null!;
    private Mock<IDdbWrapper> _ddb = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "rq01-thumbnail-host", Guid.NewGuid().ToString("N"));
        _datasets = Path.Combine(_root, "datasets");
        _temp = Path.Combine(_root, "temp");
        Directory.CreateDirectory(_datasets);
        Directory.CreateDirectory(_temp);

        _ddb = new Mock<IDdbWrapper>(MockBehavior.Loose);
        _ddb.Setup(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>())).Returns(ThumbnailBytes);
        _ddb.SetupGet(d => d.ThumbnailMimeType).Returns(MimeType);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private ThumbnailRequestHandler CreateHandler(string? token = Token) =>
        new(token!, [_datasets, _temp], _ddb.Object, NullLogger.Instance);

    private string CreateFile(params string[] segments)
    {
        var path = Path.Combine([_root, ..segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    private static async Task<HttpContext> SendAsync(ThumbnailRequestHandler handler, string path,
        string? token, int size = 64)
    {
        var context = new DefaultHttpContext();
        var body = $"path={Uri.EscapeDataString(path)}&size={size}";
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = ThumbnailRequestHandler.Route;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (token != null)
            context.Request.Headers[RemoteThumbnailGeneratorSettings.TokenHeaderName] = token;
        context.Response.Body = new MemoryStream();

        await handler.HandleAsync(context);
        return context;
    }

    private void GeneratorNeverCalled() =>
        _ddb.Verify(d => d.GenerateThumbnail(It.IsAny<string>(), It.IsAny<int>()), Times.Never);

    // --- Token ---

    [Test]
    public async Task MissingToken_Returns401()
    {
        var file = CreateFile("temp", "sample.png");

        var context = await SendAsync(CreateHandler(), file, token: null);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        GeneratorNeverCalled();
    }

    [Test]
    public async Task WrongToken_Returns401()
    {
        var file = CreateFile("temp", "sample.png");

        var context = await SendAsync(CreateHandler(), file, token: "not-the-token");

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        GeneratorNeverCalled();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void EmptyConfiguredToken_FailsClosed(string? configured)
    {
        Should.Throw<ArgumentException>(() => CreateHandler(configured));
    }

    // --- Path confinement ---

    [TestCase("outside", "secret.txt", TestName = "Outside")]
    [TestCase("temp2", "secret.txt", TestName = "PrefixSibling_temp2")]
    [TestCase("datasets-old", "secret.txt", TestName = "PrefixSibling_datasets-old")]
    public async Task PathOutsideRoots_Returns403(string folder, string name)
    {
        var file = CreateFile(folder, name);

        var context = await SendAsync(CreateHandler(), file, Token);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        GeneratorNeverCalled();
    }

    [Test]
    public async Task TraversalOutOfTemp_Returns403()
    {
        CreateFile("outside", "secret.txt");
        var traversal = _temp + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar +
                        "outside" + Path.DirectorySeparatorChar + "secret.txt";

        var context = await SendAsync(CreateHandler(), traversal, Token);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        GeneratorNeverCalled();
    }

    [Test]
    public async Task SystemFile_Returns403()
    {
        const string passwd = "/etc/passwd";
        Assume.That(File.Exists(passwd), "needs /etc/passwd (Unix)");

        var context = await SendAsync(CreateHandler(), passwd, Token);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        GeneratorNeverCalled();
    }

    // --- Accepted requests ---

    [Test]
    public async Task ValidTokenAndPathUnderTemp_ReturnsThumbnail()
    {
        var file = CreateFile("temp", "sample.png");

        var context = await SendAsync(CreateHandler(), file, Token, size: 100);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        context.Response.ContentType.ShouldBe(MimeType);
        ((MemoryStream)context.Response.Body).ToArray().ShouldBe(ThumbnailBytes);
        _ddb.Verify(d => d.GenerateThumbnail(file, 100), Times.Once);
    }

    [Test]
    public async Task ValidTokenAndPathUnderDatasets_ReturnsThumbnail()
    {
        var file = CreateFile("datasets", "org", Guid.NewGuid().ToString("N"), "image.jpg");

        var context = await SendAsync(CreateHandler(), file, Token);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        _ddb.Verify(d => d.GenerateThumbnail(file, 64), Times.Once);
    }

    // --- Client ---

    [Test]
    public void Client_Request_CarriesTheToken()
    {
        var settings = new RemoteThumbnailGeneratorSettings { Url = "http://thumbs:5080/generate-thumbnail", Token = Token };

        using var request = RemoteThumbnailGenerator.CreateRequest(settings, "/data/temp/x.png", 64);

        request.Headers.TryGetValues(RemoteThumbnailGeneratorSettings.TokenHeaderName, out var values)
            .ShouldBeTrue("the client must send the shared token");
        values!.Single().ShouldBe(Token);
    }

    [Test]
    public async Task Client_Request_IsAcceptedByTheHost()
    {
        var file = CreateFile("temp", "sample.png");
        var handler = CreateHandler();

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web.UseTestServer().Configure(app => app.Run(handler.HandleAsync)))
            .StartAsync();
        using var client = host.GetTestClient();

        var settings = new RemoteThumbnailGeneratorSettings
        {
            Url = new Uri(client.BaseAddress!, ThumbnailRequestHandler.Route).ToString(),
            Token = Token
        };
        using var request = RemoteThumbnailGenerator.CreateRequest(settings, file, 64);
        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(ThumbnailBytes);
    }

    // --- Shared containment helper ---

    [TestCase("datasets", "datasets/a/b.png", true)]
    [TestCase("datasets", "datasets", false)]
    [TestCase("datasets", "datasets2/b.png", false)]
    [TestCase("datasets", "datasets/../outside/b.png", false)]
    [TestCase("datasets", "outside/b.png", false)]
    public void IsUnder_ComparesNormalisedPaths(string root, string candidate, bool expected)
    {
        string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        CommonUtils.IsUnder(Full(root), Full(candidate)).ShouldBe(expected);
    }

    [Test]
    public void IsUnder_RootWithTrailingSeparator_StillMatches()
    {
        CommonUtils.IsUnder(_datasets + Path.DirectorySeparatorChar, Path.Combine(_datasets, "x")).ShouldBeTrue();
    }

    [Test]
    public void IsUnder_CaseFollowsTheOs()
    {
        var upper = Path.Combine(_datasets.ToUpperInvariant(), "x");
        CommonUtils.IsUnder(_datasets, upper).ShouldBe(OperatingSystem.IsWindows());
    }

    [TestCase(null, "/x")]
    [TestCase("/x", null)]
    [TestCase("", "/x")]
    public void IsUnder_EmptyPaths_AreNeverInside(string? root, string? candidate)
    {
        CommonUtils.IsUnder(root!, candidate!).ShouldBeFalse();
    }

    [Test]
    public void ValidateRelativePath_StillRejectsTraversalAndAcceptsNested()
    {
        Should.NotThrow(() => CommonUtils.ValidateRelativePath("a/b.png", _datasets));
        Should.Throw<ArgumentException>(() => CommonUtils.ValidateRelativePath("../datasets2/b.png", _datasets));
        Should.Throw<ArgumentException>(() => CommonUtils.ValidateRelativePath("/etc/passwd", _datasets));
    }
}
