using System;
using System.IO;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <see cref="StreamedDownloadResult"/> and its <see cref="ContentDispositionHeader"/>: RFC 6266
/// <c>filename*</c> with the exact UTF-8 name next to an ASCII, quoted, escaped <c>filename</c>.
/// </summary>
[TestFixture]
public class StreamedDownloadResultTests
{
    [TestCase("org-ds-ddb.zip", "attachment; filename=\"org-ds-ddb.zip\"; filename*=UTF-8''org-ds-ddb.zip")]
    [TestCase("città 2026.zip", "attachment; filename=\"citt_ 2026.zip\"; filename*=UTF-8''citt%C3%A0%202026.zip")]
    [TestCase("a\"b.zip", "attachment; filename=\"a\\\"b.zip\"; filename*=UTF-8''a%22b.zip")]
    [TestCase("a;b.zip", "attachment; filename=\"a;b.zip\"; filename*=UTF-8''a%3Bb.zip")]
    [TestCase("a\\b'c.zip", "attachment; filename=\"a\\\\b'c.zip\"; filename*=UTF-8''a%5Cb%27c.zip")]
    [TestCase("日本.tif", "attachment; filename=\"__.tif\"; filename*=UTF-8''%E6%97%A5%E6%9C%AC.tif")]
    public void Attachment_HasTheAsciiFallback_AndTheExactUtf8Name(string name, string header) =>
        ContentDispositionHeader.Attachment(name).ShouldBe(header);

    [TestCase("città 2026.zip")]
    [TestCase("a\"b;c\\d.zip")]
    [TestCase("日本 (copy).tif")]
    public void Attachment_IsReadBackAsTheSameName(string name)
    {
        // A client that knows filename* (System.Net.Http here) gets the exact name back
        var parsed = ContentDispositionHeaderValue.Parse(ContentDispositionHeader.Attachment(name));

        parsed.DispositionType.ShouldBe("attachment");
        parsed.FileNameStar.ShouldBe(name);
    }

    private static (DefaultHttpContext Http, ActionContext Action) Context(CancellationToken aborted = default)
    {
        var http = new DefaultHttpContext { RequestAborted = aborted };
        http.Response.Body = new MemoryStream();
        return (http, new ActionContext(http, new RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()));
    }

    private static FileStreamDescriptor Download(IZipArchiveBuilder zip) =>
        new("sel ü.zip", "application/zip", Mock.Of<IDDB>(), ["a.jpg"], [], FileDescriptorType.Multiple,
            NullLogger.Instance, zip);

    [Test]
    public async Task Execute_WritesTheArchive_WithTheDownloadHeaders()
    {
        var zip = new Mock<IZipArchiveBuilder>();
        zip.Setup(z => z.WriteZipAsync(It.IsAny<IDDB>(), It.IsAny<string[]>(), It.IsAny<string[]>(), false,
                It.IsAny<Stream>(), null, It.IsAny<CancellationToken>()))
            .Returns((IDDB _, string[] _, string[] _, bool _, Stream output, IProgress<long>? _, CancellationToken ct) =>
                output.WriteAsync("zip"u8.ToArray(), ct).AsTask());
        var (http, action) = Context();

        await new StreamedDownloadResult(Download(zip.Object), inline: false, NullLogger.Instance).ExecuteResultAsync(action);

        http.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        http.Response.ContentType.ShouldBe("application/zip");
        http.Response.Headers.CacheControl.ToString().ShouldBe("no-store");
        http.Response.Headers.ContentDisposition.ToString()
            .ShouldBe("attachment; filename=\"sel _.zip\"; filename*=UTF-8''sel%20%C3%BC.zip");
        ((MemoryStream)http.Response.Body).ToArray().ShouldBe("zip"u8.ToArray());
    }

    [Test]
    public async Task Execute_Inline_HasNoFileName()
    {
        var (http, action) = Context();

        await new StreamedDownloadResult(Download(Mock.Of<IZipArchiveBuilder>()), inline: true, NullLogger.Instance)
            .ExecuteResultAsync(action);

        http.Response.Headers.ContentDisposition.ToString().ShouldBe("inline");
    }

    [Test]
    public async Task Execute_ClientGone_IsNotAnError()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var zip = new Mock<IZipArchiveBuilder>();
        zip.Setup(z => z.WriteZipAsync(It.IsAny<IDDB>(), It.IsAny<string[]>(), It.IsAny<string[]>(), false,
                It.IsAny<Stream>(), null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var (_, action) = Context(aborted.Token);

        await Should.NotThrowAsync(() =>
            new StreamedDownloadResult(Download(zip.Object), inline: false, NullLogger.Instance).ExecuteResultAsync(action));
    }
}
