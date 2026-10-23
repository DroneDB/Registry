#nullable enable
using Registry.Web.Test.Support;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Controllers;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Objects;
using Registry.Web.Utilities;
using Shouldly;


namespace Registry.Web.Test;

/// <summary>
/// REG-03: tiles and thumbnails carry an ETag derived from the entry hash and a private
/// Cache-Control; a matching If-None-Match is answered with 304 after the access check but
/// before any cache lookup or rendering.
/// </summary>
[TestFixture]
public class TileHttpCachingTests
{
    private const string Hash = "8f2a9c";
    private static readonly byte[] Bytes = [0x52, 0x49, 0x46, 0x46];

    private Mock<IObjectRenderService> _manager = null!;
    private ReadableObject _obj = null!;

    [SetUp]
    public void SetUp()
    {
        _manager = new Mock<IObjectRenderService>();
        _obj = new ReadableObject("org", "ds", "ortho.tif", Hash, "ortho.tif", Mock.Of<IDDB>());
        _manager.Setup(m => m.ResolveReadableObjectAsync("org", "ds", "ortho.tif", It.IsAny<CancellationToken>())).ReturnsAsync(_obj);
        _manager.Setup(m => m.ResolveThumbnailObjectAsync("org", "ds", "ortho.tif", It.IsAny<CancellationToken>())).ReturnsAsync(_obj);
        _manager.Setup(m => m.GenerateTileDataAsync(_obj, 19, 1, 2, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageDataDto { Data = Bytes, ContentType = "image/webp", Name = "2.webp" });
        _manager.Setup(m => m.GenerateThumbnailDataAsync(_obj, 256, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageDataDto { Data = Bytes, ContentType = "image/webp", Name = "ortho.webp" });
    }

    private ObjectsController CreateController(string? ifNoneMatch = null)
    {
        var controller = new ObjectsController(Mock.Of<IEntryQueryService>(), Mock.Of<IEntryCommandService>(),
            Mock.Of<IEntryTransferService>(), _manager.Object, NullLogger<ObjectsController>.Instance,
            TestSettings.Options<ThumbnailOptions>(Microsoft.Extensions.Options.Options.Create(new AppSettings())))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (ifNoneMatch != null)
            controller.Request.Headers.IfNoneMatch = ifNoneMatch;
        return controller;
    }

    private void VerifyNeverRendered()
    {
        _manager.Verify(m => m.GenerateTileDataAsync(It.IsAny<ReadableObject>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _manager.Verify(m => m.GenerateThumbnailDataAsync(It.IsAny<ReadableObject>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task GenerateTile_SetsEtagAndCacheControl()
    {
        var controller = CreateController();
        var result = await controller.GenerateTile("org", "ds", 19, 1, "2", "ortho.tif", "png");

        result.ShouldBeOfType<FileContentResult>().FileContents.ShouldBe(Bytes);
        controller.Response.Headers.ETag.ToString().ShouldStartWith($"\"{Hash}-tile-19-1-2-1-");
        controller.Response.Headers.CacheControl.ToString().ShouldBe("private, no-cache");
    }

    [Test]
    public async Task GenerateTile_WithCurrentVersion_IsImmutable()
    {
        var controller = CreateController();
        await controller.GenerateTile("org", "ds", 19, 1, "2", "ortho.tif", "png", v: Hash);

        controller.Response.Headers.CacheControl.ToString().ShouldBe("private, max-age=86400, immutable");
    }

    [Test]
    public async Task GenerateTile_WithStaleVersion_MustRevalidate()
    {
        var controller = CreateController();
        await controller.GenerateTile("org", "ds", 19, 1, "2", "ortho.tif", "png", v: "old-hash");

        controller.Response.Headers.CacheControl.ToString().ShouldBe("private, no-cache");
    }

    [Test]
    public async Task GenerateTile_IfNoneMatch_Returns304WithoutRendering()
    {
        var first = CreateController();
        await first.GenerateTile("org", "ds", 19, 1, "2", "ortho.tif", "png");
        var etag = first.Response.Headers.ETag.ToString();

        var second = CreateController(etag);
        var result = await second.GenerateTile("org", "ds", 19, 1, "2", "ortho.tif", "png");

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status304NotModified);
        second.Response.Headers.ETag.ToString().ShouldBe(etag);
        second.Response.Headers.CacheControl.ToString().ShouldBe("private, no-cache");
        _manager.Verify(m => m.ResolveReadableObjectAsync("org", "ds", "ortho.tif", It.IsAny<CancellationToken>()), Times.Exactly(2));
        _manager.Verify(m => m.GenerateTileDataAsync(It.IsAny<ReadableObject>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GenerateTile_IfNoneMatchForAnotherTile_Renders()
    {
        var controller = CreateController("\"" + Hash + "-tile-19-1-3-1-0\"");
        var result = await controller.GenerateTile("org", "ds", 19, 1, "2", "ortho.tif", "png");

        result.ShouldBeOfType<FileContentResult>();
    }

    [Test]
    public async Task GenerateTile_AccessDenied_Throws_EvenWithMatchingEtag()
    {
        var etagController = CreateController();
        await etagController.GenerateTile("org", "ds", 19, 1, "2", "ortho.tif", "png");
        var etag = etagController.Response.Headers.ETag.ToString();

        _manager.Setup(m => m.ResolveReadableObjectAsync("org", "ds", "ortho.tif", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Registry.Web.Exceptions.UnauthorizedException("no"));

        await Should.ThrowAsync<Registry.Web.Exceptions.UnauthorizedException>(
            () => CreateController(etag).GenerateTile("org", "ds", 19, 1, "2", "ortho.tif", "png"));
    }

    [Test]
    public async Task GenerateThumbnail_SetsEtagAndCacheControl_And304WithoutRendering()
    {
        var first = CreateController();
        var result = await first.GenerateThumbnail("org", "ds", "ortho.tif", 256);
        result.ShouldBeOfType<FileContentResult>();
        var etag = first.Response.Headers.ETag.ToString();
        etag.ShouldStartWith($"\"{Hash}-thumb-256-");
        first.Response.Headers.CacheControl.ToString().ShouldBe("private, no-cache");

        _manager.Invocations.Clear();
        var second = CreateController(etag);
        (await second.GenerateThumbnail("org", "ds", "ortho.tif", 256))
            .ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(StatusCodes.Status304NotModified);
        VerifyNeverRendered();
    }

    [Test]
    public async Task GenerateThumbnail_ForDataset_HasNoValidator()
    {
        _manager.Setup(m => m.GenerateThumbnailDataAsync("org", "ds", null, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new StorageDataDto { Data = Bytes, ContentType = "image/webp", Name = "thumbnail.webp" });

        var controller = CreateController();
        (await controller.GenerateThumbnail("org", "ds", null!, null)).ShouldBeOfType<FileContentResult>();

        controller.Response.Headers.ETag.ToString().ShouldBeEmpty();
        _manager.Verify(m => m.ResolveReadableObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
        _manager.Verify(m => m.ResolveThumbnailObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public void Policy_IfNoneMatch_HandlesListsWeakTagsAndWildcard()
    {
        var policy = HashedContentCachePolicy.For(Hash, null, "tile", 1, 2, 3);
        var etag = policy.ETag.ToString();

        bool Check(string header)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers.IfNoneMatch = header;
            return policy.IsNotModified(ctx.Request);
        }

        Check(etag).ShouldBeTrue();
        Check($"\"other\", {etag}").ShouldBeTrue();
        Check("W/" + etag).ShouldBeTrue();
        Check("*").ShouldBeTrue();
        Check("\"other\"").ShouldBeFalse();
        Check("not-an-etag").ShouldBeFalse();
        policy.IsNotModified(new DefaultHttpContext().Request).ShouldBeFalse();
    }

    [Test]
    public void Policy_EtagChangesWithHashAndVariant()
    {
        var a = HashedContentCachePolicy.For("h1", null, "tile", 1, 2, 3).ETag;
        HashedContentCachePolicy.For("h2", null, "tile", 1, 2, 3).ETag.ShouldNotBe(a);
        HashedContentCachePolicy.For("h1", null, "tile", 1, 2, 4).ETag.ShouldNotBe(a);
        HashedContentCachePolicy.For("h1", "h1", "tile", 1, 2, 3).ETag.ShouldBe(a, "v only affects freshness");
    }
}
