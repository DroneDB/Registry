using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The bytes a user put in a dataset are served from the origin of the Hub, where the session token can be read:
/// a file a browser renders as a document (<c>index.html</c> of a <c>.3tz</c>, an <c>.svg</c> upload opened in a
/// tab) would run the user's script with it. <see cref="UserContentPolicy"/> keeps the type of what the Hub reads
/// (tiles, images, PDF, media) and sends everything else as an attachment of <c>application/octet-stream</c>.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserContent"
/// </summary>
[TestFixture]
public class UserContentPolicyTests
{
    [TestCase("3dtiles/index.html")]
    [TestCase("3dtiles/INDEX.HTM")]
    [TestCase("a/logo.svg")]
    [TestCase("a/logo.svgz")]
    [TestCase("a/app.js")]
    [TestCase("a/app.mjs")]
    [TestCase("a/feed.xml")]
    [TestCase("a/page.xhtml")]
    [TestCase("a/doc.mht")]
    [TestCase("a/noextension")]
    [TestCase("a/index.html.")]
    public void BuildFile_ThatABrowserRendersAsADocument_IsAnAttachmentOfOctetStream(string path)
    {
        var served = UserContentPolicy.ForBuildFile(path);

        served.MediaType.ShouldBe("application/octet-stream");
        served.Inline.ShouldBeFalse();
        served.ContentSecurityPolicy.ShouldBe("sandbox");
    }

    [Test]
    public void BuildFile_WhoseBuilderDeclaresADocumentType_IsStillAnAttachmentOfOctetStream()
    {
        var served = UserContentPolicy.ForBuildFile("3dtiles/index.html", "text/html");

        served.MediaType.ShouldBe("application/octet-stream");
        served.Inline.ShouldBeFalse();
    }

    [Test]
    public void BuildFile_WhoseNameSaysImageButBuilderDeclaresHtml_KeepsTheTypeOfItsExtension()
    {
        var served = UserContentPolicy.ForBuildFile("t/0.png", "text/html");

        served.MediaType.ShouldBe("image/png");
    }

    [TestCase("tileset.json", "application/json")]
    [TestCase("t/0.b3dm", "application/octet-stream")]
    [TestCase("t/0.I3DM", "application/octet-stream")]
    [TestCase("t/0.pnts", "application/octet-stream")]
    [TestCase("t/0.cmpt", "application/octet-stream")]
    [TestCase("t/0.subtree", "application/octet-stream")]
    [TestCase("t/0.glb", "model/gltf-binary")]
    [TestCase("t/0.gltf", "model/gltf+json")]
    [TestCase("t/0.bin", "application/octet-stream")]
    [TestCase("t/0.jpg", "image/jpeg")]
    [TestCase("t/0.jpeg", "image/jpeg")]
    [TestCase("t/0.png", "image/png")]
    [TestCase("t/0.webp", "image/webp")]
    [TestCase("t/0.ktx2", "image/ktx2")]
    [TestCase("t/0.basis", "application/octet-stream")]
    [TestCase("t/0.avif", "image/avif")]
    [TestCase("t/0.gif", "image/gif")]
    [TestCase("cog/cog.tif", "image/tiff")]
    public void BuildFile_OfATypeTheHubReads_KeepsItsTypeAndStaysInline(string path, string mediaType)
    {
        var served = UserContentPolicy.ForBuildFile(path);

        served.MediaType.ShouldBe(mediaType);
        served.Inline.ShouldBeTrue();
        served.ContentSecurityPolicy.ShouldBe("sandbox");
    }

    [Test]
    public void BuildFile_WithADeclaredTypeOfItsKind_KeepsTheDeclaredParameters()
    {
        const string cog = "image/tiff; application=geotiff; profile=cloud-optimized";

        UserContentPolicy.ForBuildFile("cog/cog.tif", cog).MediaType.ShouldBe(cog);
    }

    [TestCase("a/page.html")]
    [TestCase("a/logo.svg")]
    [TestCase("a/app.js")]
    [TestCase("a/feed.xml")]
    [TestCase("a/style.css")]
    [TestCase("a/data.zip")]
    [TestCase("a/photo.heic")]
    public void Entry_ThatABrowserRendersAsADocument_OrIsNotShownInPlace_IsAnAttachmentOfOctetStream(string path)
    {
        var served = UserContentPolicy.ForEntry(path);

        served.MediaType.ShouldBe("application/octet-stream");
        served.Inline.ShouldBeFalse();
    }

    // What the Hub opens in a tab, a lightbox or a viewer must stay in place
    [TestCase("photo.JPG", "image/jpeg")]
    [TestCase("photo.png", "image/png")]
    [TestCase("clip.mp4", "video/mp4")]
    [TestCase("clip.webm", "video/webm")]
    [TestCase("song.mp3", "audio/mpeg")]
    [TestCase("report.pdf", "application/pdf")]
    [TestCase("notes.txt", "text/plain")]
    public void Entry_OfATypeTheHubShowsInPlace_KeepsItsTypeAndStaysInline(string path, string mediaType)
    {
        var served = UserContentPolicy.ForEntry(path);

        served.MediaType.ShouldBe(mediaType);
        served.Inline.ShouldBeTrue();
    }

    // Every extension libddb makes an Image or a Video entry of: the lightbox plays a video from the download URL
    // and opens a photo or a video in a tab, so none of them may turn into an attachment
    [TestCase("a.tif", "image/tiff")]
    [TestCase("a.tiff", "image/tiff")]
    [TestCase("a.jp2", "image/jp2")]
    [TestCase("a.j2k", "image/j2c")]
    [TestCase("a.jpg", "image/jpeg")]
    [TestCase("a.jpeg", "image/jpeg")]
    [TestCase("a.dng", "image/x-adobe-dng")]
    [TestCase("a.png", "image/png")]
    [TestCase("a.gif", "image/gif")]
    [TestCase("a.webp", "image/webp")]
    [TestCase("a.mp4", "video/mp4")]
    [TestCase("a.mov", "video/quicktime")]
    [TestCase("a.webm", "video/webm")]
    [TestCase("a.m4v", "video/mp4")]
    [TestCase("a.avi", "video/x-msvideo")]
    [TestCase("a.MKV", "video/x-matroska")]
    public void Entry_ThatLibddbTypesAsAnImageOrAVideo_StaysInline(string path, string mediaType)
    {
        var served = UserContentPolicy.ForEntry(path);

        served.MediaType.ShouldBe(mediaType);
        served.Inline.ShouldBeTrue();
    }

    // The PDF viewer of Chrome does not render under a sandbox, and audio and video have nothing of the user's to
    // run; whatever else is shown in place is confined
    [TestCase("report.pdf", null)]
    [TestCase("clip.mp4", null)]
    [TestCase("clip.mkv", null)]
    [TestCase("clip.avi", null)]
    [TestCase("song.mp3", null)]
    [TestCase("photo.png", "sandbox")]
    [TestCase("notes.txt", "sandbox")]
    [TestCase("page.html", "sandbox")]
    public void Entry_IsSandboxed_ExceptTheTypesWhoseViewerNeedsNone(string path, string? policy)
    {
        UserContentPolicy.ForEntry(path).ContentSecurityPolicy.ShouldBe(policy);
        UserContentPolicy.ContentSecurityPolicyFor(UserContentPolicy.ForEntry(path).MediaType).ShouldBe(policy);
    }
}

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/>: the build files and the raw files of a dataset are sent as
/// <see cref="UserContentPolicy"/> says, with ranges, validators and 304 working as before.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserContentHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class UserContentHttpTests : IDisposable
{
    private const string OwnerUser = "usercontent-owner";
    private const string Org = "usercontent-org";
    private const string Ds = "usercontent-ds";
    private const string Hash = "0b9d2fe5c7a14d5d9a4fd3ec2b1a8e3f6c5d4e3b2a1908f7e6d5c4b3a2918070";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _datasetFolder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
        _datasetFolder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef).DatasetFolderPath;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<HttpResponseMessage> GetAsync(string url, Action<HttpRequestMessage>? configure = null,
        HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, url).AsUser(OwnerUser);
        configure?.Invoke(request);
        return await _client.SendAsync(request);
    }

    // A file of the build, as libddb resolves it
    private string BuildFile(string relative, string content, string? mediaType = null)
    {
        var path = Path.Combine(_datasetFolder, ".ddb", "build", Hash, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(_datasetFolder, Hash, relative))
            .Returns(new ResolvedBuildFile(path, mediaType));
        return path;
    }

    private static string BuildFileUrl(string relative) => $"/orgs/{Org}/ds/{Ds}/build/{Hash}/{relative}";

    // A raw file of the dataset
    private void RawFile(string path, string content)
    {
        File.WriteAllText(Path.Combine(_datasetFolder, path), content);
        _factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, path))
            .Returns(new Entry { Path = path, Hash = new string('a', 64), Type = EntryType.Generic, Size = content.Length });
    }

    private static string RawUrl(string path, bool inline) =>
        $"/orgs/{Org}/ds/{Ds}/download/{path}" + (inline ? "?inline=1" : "");

    [TestCase("3dtiles/index.html")]
    [TestCase("3dtiles/logo.svg")]
    [TestCase("3dtiles/app.js")]
    public async Task BuildFile_ThatABrowserRendersAsADocument_IsAnOctetStreamAttachment(string relative)
    {
        BuildFile(relative, "<script>steal(localStorage)</script>", "text/html");

        using var get = await GetAsync(BuildFileUrl(relative));
        using var head = await GetAsync(BuildFileUrl(relative), method: HttpMethod.Head);

        foreach (var response in new[] { get, head })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
            response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
            response.Content.Headers.ContentDisposition!.FileName.ShouldBe(Path.GetFileName(relative));
            response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        }
    }

    [TestCase("3dtiles/tileset.json", "application/json")]
    [TestCase("3dtiles/tiles/0.b3dm", "application/octet-stream")]
    [TestCase("3dtiles/tiles/0.png", "image/png")]
    public async Task BuildFile_OfATypeTheHubReads_KeepsItsTypeAndIsNotAnAttachment(string relative, string mediaType)
    {
        BuildFile(relative, "bytes");

        using var response = await GetAsync(BuildFileUrl(relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(mediaType);
        response.Content.Headers.ContentDisposition.ShouldBeNull();
    }

    [TestCase("3dtiles/index.html", "text/html")]
    [TestCase("3dtiles/tileset.json", "application/json")]
    [TestCase("3dtiles/tiles/0.b3dm", null)]
    [TestCase("gsplat/model.rad", "application/octet-stream")]
    public async Task BuildFile_EveryResponse_IsSandboxed(string relative, string? mediaType)
    {
        BuildFile(relative, "bytes", mediaType);

        using var get = await GetAsync(BuildFileUrl(relative));
        using var head = await GetAsync(BuildFileUrl(relative), method: HttpMethod.Head);
        using var notModified = await GetAsync(BuildFileUrl(relative), r => r.Headers.IfNoneMatch.Add(get.Headers.ETag!));
        using var range = await GetAsync(BuildFileUrl(relative), r => r.Headers.Range = new RangeHeaderValue(0, 1));
        using var missing = await GetAsync(BuildFileUrl("cog/missing.tif"));

        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        foreach (var response in new[] { get, head, notModified, range })
            response.Headers.GetValues("Content-Security-Policy").ShouldBe(["sandbox"], response.StatusCode.ToString());
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A tile read by ranges keeps answering 206 with the bytes asked for, and its validators still work
    [Test]
    public async Task BuildFile_OfAnAllowedType_StillAnswersRangesAndValidators()
    {
        BuildFile("3dtiles/tiles/1.b3dm", "0123456789");

        using var whole = await GetAsync(BuildFileUrl("3dtiles/tiles/1.b3dm"));
        using var range = await GetAsync(BuildFileUrl("3dtiles/tiles/1.b3dm"), r => r.Headers.Range = new RangeHeaderValue(2, 5));
        using var notModified = await GetAsync(BuildFileUrl("3dtiles/tiles/1.b3dm"), r => r.Headers.IfNoneMatch.Add(whole.Headers.ETag!));

        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await range.Content.ReadAsStringAsync()).ShouldBe("2345");
        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        notModified.Headers.ETag.ShouldBe(whole.Headers.ETag);
    }

    // The attachment of a file the policy does not allow keeps ranges and validators too
    [Test]
    public async Task BuildFile_OfAnotherType_AnswersRangesAndValidators_AsAnAttachment()
    {
        BuildFile("3dtiles/index.html", "0123456789");

        using var whole = await GetAsync(BuildFileUrl("3dtiles/index.html"));
        using var range = await GetAsync(BuildFileUrl("3dtiles/index.html"), r => r.Headers.Range = new RangeHeaderValue(2, 5));
        using var notModified = await GetAsync(BuildFileUrl("3dtiles/index.html"), r => r.Headers.IfNoneMatch.Add(whole.Headers.ETag!));

        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await range.Content.ReadAsStringAsync()).ShouldBe("2345");
        range.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Test]
    public async Task RawDownload_OfHtml_OpenedInline_IsNotRenderedByTheBrowser()
    {
        RawFile("page.html", "<script>steal(localStorage)</script>");

        using var response = await GetAsync(RawUrl("page.html", inline: true));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("page.html");
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["sandbox"]);
    }

    [Test]
    public async Task RawDownload_OfSvg_AsAnAttachment_IsOctetStream()
    {
        RawFile("logo.svg", "<svg onload='steal()'/>");

        using var response = await GetAsync(RawUrl("logo.svg", inline: false));

        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
    }

    // The Hub opens a PDF in a frame and a photo or a text in a tab: those stay in place, a PDF without the sandbox
    // its viewer cannot run under
    [TestCase("report.pdf", "application/pdf", null)]
    [TestCase("photo.png", "image/png", "sandbox")]
    [TestCase("notes.txt", "text/plain", "sandbox")]
    public async Task RawDownload_OfATypeTheHubShowsInPlace_StaysInline(string path, string mediaType, string? policy)
    {
        RawFile(path, "0123456789");

        using var whole = await GetAsync(RawUrl(path, inline: true));
        using var range = await GetAsync(RawUrl(path, inline: true), r => r.Headers.Range = new RangeHeaderValue(2, 5));

        whole.Content.Headers.ContentType!.MediaType.ShouldBe(mediaType);
        whole.Content.Headers.ContentDisposition.ShouldBeNull();
        whole.Headers.TryGetValues("Content-Security-Policy", out var csp).ShouldBe(policy is not null);
        csp?.ShouldBe([policy!]);
        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await range.Content.ReadAsStringAsync()).ShouldBe("2345");
    }

    // The lightbox plays a video from the download URL: every video type libddb knows stays in place, unsandboxed
    [TestCase("clip.mkv", "video/x-matroska")]
    [TestCase("clip.avi", "video/x-msvideo")]
    [TestCase("clip.mp4", "video/mp4")]
    public async Task RawDownload_OfAVideo_OpenedInline_StaysInlineWithoutASandbox(string path, string mediaType)
    {
        RawFile(path, "0123456789");

        using var response = await GetAsync(RawUrl(path, inline: true));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(mediaType);
        response.Content.Headers.ContentDisposition.ShouldBeNull();
        response.Headers.Contains("Content-Security-Policy").ShouldBeFalse();
    }

    // A selection is streamed as a ZIP archive, which is an attachment even when the client asks for it in place
    [Test]
    public async Task RawDownload_OfASelection_AskedInline_IsASandboxedZipAttachment()
    {
        RawFile("page.html", "<script>steal(localStorage)</script>");
        RawFile("photo.png", "png");

        using var response = await GetAsync($"/orgs/{Org}/ds/{Ds}/download?path=page.html&path=photo.png&inline=1");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition!.FileName!.ShouldEndWith(".zip");
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["sandbox"]);
    }

    // The objects route sends the raw file as an attachment whatever its type
    [Test]
    public async Task ObjectGet_OfHtml_IsAnOctetStreamAttachment()
    {
        RawFile("page.html", "<script>steal(localStorage)</script>");

        using var response = await GetAsync($"/orgs/{Org}/ds/{Ds}/obj",
            r => r.Content = new FormUrlEncodedContent([new("path", "page.html")]));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("page.html");
        response.Headers.GetValues("Content-Security-Policy").ShouldBe(["sandbox"]);
    }

    [Test]
    public async Task RawDownload_OfAnAllowedType_AsAnAttachment_KeepsItsFileName()
    {
        RawFile("photo.png", "png");

        using var response = await GetAsync(RawUrl("photo.png", inline: false));

        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("photo.png");
    }
}
