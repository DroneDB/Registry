using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;
using SkiaSharp;

namespace Registry.Web.Test.E2eFixes;

/// <summary>
/// End-to-end check: <c>GET tiles/{z}/{x}/{y}.png</c> answered WebP
/// bytes (<c>image/webp</c>, a <c>.webp</c> download name): the render service hardcoded the format. The
/// extension of the URL chooses the format now (<c>.png</c> a real PNG, <c>.webp</c> WebP, <c>.jpg</c> JPEG), the
/// <c>Content-Type</c> and the file name match the bytes, libddb's format-aware exports encode them once
/// (<c>DDBMemoryTileFmt</c> for a plain tile, <c>DDBMemoryTileEx3</c> for a visualization tile, whose bytes Registry
/// serves as they come, without a decode and a second encode) and the format is part of the cache key and of the
/// ETag, so a PNG is never served for a WebP.
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
        // libddb encodes the format it is asked for: the bytes say which
        _factory.DdbWrapper.Setup(d => d.GenerateMemoryTile(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<string>()))
            .Returns((string _, int _, int _, int _, int _, bool _, bool _, string _, string format) =>
                Encoded(format, PlainMarker));
        _factory.DdbWrapper.Setup(d => d.GenerateMemoryTileEx(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<RasterAnalysisOptions?>(), It.IsAny<string>()))
            .Returns((string _, int _, int _, int _, int _, bool _, bool _, string _, string? _, string? _, string? _,
                string? _, string? _, string? _, RasterAnalysisOptions? _, string format) =>
                Encoded(format, VisualizationMarker));
    }

    private const string PlainMarker = "plain tile";
    private const string VisualizationMarker = "visualization tile";

    // What the fake libddb returns for a format: its signature and a marker, which no image decoder reads, so a
    // response with exactly these bytes was served as libddb encoded it (a decode and a second encode would fail or
    // give other bytes)
    private static byte[] Encoded(string format, string marker)
    {
        var tail = System.Text.Encoding.ASCII.GetBytes(marker);
        return format switch
        {
            "png" => [.. PngSignature, .. tail],
            "webp" => [.. "RIFF\0\0\0\0WEBP"u8, .. tail],
            "jpeg" => [.. JpegSignature, .. tail],
            _ => throw new DdbException($"Unsupported image format: {format}")
        };
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

    [TestCase("png", "image/png", "png", 6)]
    [TestCase("webp", "image/webp", "webp", 7)]
    [TestCase("jpg", "image/jpeg", "jpeg", 8)]
    public async Task VisualizationTile_IsTheBytesLibddbEncodedInTheFormatOfItsExtension(string extension,
        string mediaType, string ddbFormat, int row)
    {
        using var response = await GetAsync(
            $"/orgs/{Org}/ds/{Ds}/tiles-ex/18/4/{row}.{extension}?path={Raster}&colormap=viridis");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe(mediaType);
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe($"{row}.{extension}");
        // Byte for byte what libddb returned: Registry asks libddb for the format and does not encode the tile again
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Encoded(ddbFormat, VisualizationMarker));
        _factory.DdbWrapper.Verify(d => d.GenerateMemoryTileEx(It.IsAny<string>(), 18, 4, row, It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), "viridis", It.IsAny<string?>(), It.IsAny<RasterAnalysisOptions?>(),
            ddbFormat), Times.Once);
    }

    // The premise of the test above: the bytes the fake libddb returns are not an image, so serving them unchanged is
    // the only way to answer 200 with them
    [TestCase("png")]
    [TestCase("webp")]
    [TestCase("jpeg")]
    public void TheFakeLibddbTile_IsNotADecodableImage(string format)
    {
        using var data = SKData.CreateCopy(Encoded(format, VisualizationMarker));
        SKCodec.Create(data).ShouldBeNull("no image codec reads the bytes");
    }

    [Test]
    public async Task VisualizationTile_TheFormatIsPartOfTheCacheKey()
    {
        var url = $"/orgs/{Org}/ds/{Ds}/tiles-ex/19/8/10";

        using var png = await GetAsync($"{url}.png?path={Raster}&colormap=viridis");
        using var webp = await GetAsync($"{url}.webp?path={Raster}&colormap=viridis");
        using var pngAgain = await GetAsync($"{url}.png?path={Raster}&colormap=viridis");

        (await png.Content.ReadAsByteArrayAsync()).ShouldBe(Encoded("png", VisualizationMarker));
        (await webp.Content.ReadAsByteArrayAsync()).ShouldBe(Encoded("webp", VisualizationMarker));
        (await pngAgain.Content.ReadAsByteArrayAsync()).ShouldBe(Encoded("png", VisualizationMarker));
        foreach (var format in new[] { "png", "webp" })
            _factory.DdbWrapper.Verify(d => d.GenerateMemoryTileEx(It.IsAny<string>(), 19, 8, 10, It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<RasterAnalysisOptions?>(), format), Times.Once, $"one {format} render; the second PNG is a cache hit");
    }

    // FR-RAS-10: a rescale that is not "min,max" with min below max is a 400 at the rescale member, not a failure of the
    // native render (the Hub saw HTTP 500s and a blank raster)
    [TestCase("abc")]
    [TestCase("1")]
    [TestCase("1,")]
    [TestCase(",5")]
    [TestCase("1,2,3")]
    [TestCase("40,10")]
    [TestCase("5,5")]
    [TestCase("NaN,5")]
    [TestCase("12,5")]
    public async Task VisualizationTile_WithAnInvalidRescale_Is400AtTheRescaleMember(string rescale)
    {
        using var response = await GetAsync(
            $"/orgs/{Org}/ds/{Ds}/tiles-ex/18/4/3.png?path={Raster}&rescale={Uri.EscapeDataString(rescale)}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["errors"]!["rescale"]![0]!.GetValue<string>().ShouldBe(
            $"The rescale value '{rescale}' is invalid: use two numbers, min and max, separated by a comma, with min less than max (for example 0,255)");
        _factory.DdbWrapper.Verify(d => d.GenerateMemoryTileEx(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<RasterAnalysisOptions?>(), It.IsAny<string>()),
            Times.Never);
    }

    [TestCase("0,255")]
    [TestCase("-1.5,2.5")]
    [TestCase(" 0 , 1e3 ")]
    public async Task VisualizationTile_WithAValidRescale_IsServed(string rescale)
    {
        using var response = await GetAsync(
            $"/orgs/{Org}/ds/{Ds}/tiles-ex/18/4/3.png?path={Raster}&rescale={Uri.EscapeDataString(rescale)}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task VisualizationThumbnail_AndExport_WithAnInvalidRescale_Are400()
    {
        using var thumb = await GetAsync($"/orgs/{Org}/ds/{Ds}/thumb-ex?path={Raster}&rescale=abc");
        using var export = await GetAsync($"/orgs/{Org}/ds/{Ds}/export?path={Raster}&rescale=abc");

        thumb.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        export.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
