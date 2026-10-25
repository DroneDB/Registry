#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Services.Objects;
using Registry.Web.Test.Support;
using Shouldly;
using SkiaSharp;

namespace Registry.Web.Test.E2eFixes;

/// <summary>
/// End-to-end check: <c>GET tiles/{z}/{x}/{y}.png</c> answered WebP
/// bytes (<c>image/webp</c>, a <c>.webp</c> download name): the render service hardcoded the format. The
/// extension of the URL chooses the format now (<c>.png</c> a real PNG, <c>.webp</c> WebP, <c>.jpg</c> JPEG), the
/// <c>Content-Type</c> and the file name match the bytes, libddb's format-aware export renders them
/// (<c>DDBMemoryTileFmt</c>; the visualization tiles, which libddb renders as PNG, are encoded in the format
/// asked for) and the format is part of the cache key and of the ETag, so a PNG is never served for a WebP.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TileFormatHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class TileFormatHttpTests : IDisposable
{
    private const string OwnerUser = "tile-format-owner";
    private const string Org = "tile-format-org";
    private const string Ds = "tile-format-ds";
    private const string Raster = "ortho.jpg";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] JpegSignature = [0xFF, 0xD8];

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _datasetFolder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
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

        _factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, Raster))
            .Returns(new Entry { Path = Raster, Hash = "tile-format-hash", Type = EntryType.GeoImage, Size = 3 });
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _factory.DdbWrapper.Invocations.Clear();
        // libddb renders the format it is asked for: the bytes say which
        _factory.DdbWrapper.Setup(d => d.GenerateMemoryTile(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns((string _, int _, int _, int _, int _, bool _, bool _, string _, string format) =>
                format switch
                {
                    "png" => [.. PngSignature, 1],
                    "webp" => "RIFF\0\0\0\0WEBP"u8.ToArray(),
                    "jpeg" => [.. JpegSignature, 2],
                    _ => throw new DdbException($"Unsupported image format: {format}")
                });
        _factory.DdbWrapper.Setup(d => d.GenerateMemoryTileEx(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(() => SolidPng());
    }

    private static byte[] SolidPng()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private async Task<HttpResponseMessage> GetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url).AsUser(OwnerUser);
        return await _client.SendAsync(request);
    }

    private static void ShouldBeLike(byte[] bytes, string extension)
    {
        switch (extension)
        {
            case "png": bytes.Take(4).ShouldBe(PngSignature); break;
            case "jpg": bytes.Take(2).ShouldBe(JpegSignature); break;
            default:
                System.Text.Encoding.ASCII.GetString(bytes, 0, 4).ShouldBe("RIFF");
                System.Text.Encoding.ASCII.GetString(bytes, 8, 4).ShouldBe("WEBP");
                break;
        }
    }

    [TestCase("png", "image/png", 3)]
    [TestCase("webp", "image/webp", 4)]
    [TestCase("jpg", "image/jpeg", 5)]
    public async Task Tile_IsServedInTheFormatOfItsExtension(string extension, string mediaType, int row)
    {
        using var response = await GetAsync($"/orgs/{Org}/ds/{Ds}/tiles/18/4/{row}.{extension}?path={Raster}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(mediaType);
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe($"{row}.{extension}");
        ShouldBeLike(await response.Content.ReadAsByteArrayAsync(), extension);
    }

    [Test]
    public async Task Tile_TheFormatIsPartOfTheCacheKey_AndOfTheETag()
    {
        var url = $"/orgs/{Org}/ds/{Ds}/tiles/19/8/9";

        using var png = await GetAsync($"{url}.png?path={Raster}");
        using var webp = await GetAsync($"{url}.webp?path={Raster}");
        using var pngAgain = await GetAsync($"{url}.png?path={Raster}");

        ShouldBeLike(await png.Content.ReadAsByteArrayAsync(), "png");
        ShouldBeLike(await webp.Content.ReadAsByteArrayAsync(), "webp");
        ShouldBeLike(await pngAgain.Content.ReadAsByteArrayAsync(), "png");
        png.Headers.ETag.ShouldNotBe(webp.Headers.ETag);
        _factory.DdbWrapper.Verify(d => d.GenerateMemoryTile(It.IsAny<string>(), 19, 8, 9, It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), "png"), Times.Once, "the second PNG is a cache hit");
        _factory.DdbWrapper.Verify(d => d.GenerateMemoryTile(It.IsAny<string>(), 19, 8, 9, It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), "webp"), Times.Once);
    }

    [Test]
    public async Task Tile_OfAnotherExtension_IsNotARoute()
    {
        using var response = await GetAsync($"/orgs/{Org}/ds/{Ds}/tiles/18/4/3.gif?path={Raster}");

        response.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        _factory.DdbWrapper.Verify(d => d.GenerateMemoryTile(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(),
            It.IsAny<string>()), Times.Never);
    }

    [TestCase("png", "image/png", 6)]
    [TestCase("webp", "image/webp", 7)]
    [TestCase("jpg", "image/jpeg", 8)]
    public async Task VisualizationTile_IsEncodedInTheFormatOfItsExtension(string extension, string mediaType, int row)
    {
        using var response = await GetAsync(
            $"/orgs/{Org}/ds/{Ds}/tiles-ex/18/4/{row}.{extension}?path={Raster}&colormap=viridis");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(mediaType);
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe($"{row}.{extension}");
        ShouldBeLike(await response.Content.ReadAsByteArrayAsync(), extension);
    }
}
