using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/> (U-201, §7.1 "Raster point value and area statistics", FR-RAS-31):
/// pixel coordinates (<c>x</c>, <c>y</c>; <c>x0</c>..<c>y1</c>) are for an R-JPEG's raw thermal grid only. On a
/// georeferenced raster they are a 400 ProblemDetails with the reason under <c>errors.x</c> (<c>errors.x0</c>); on an
/// R-JPEG they reach libddb with the NoData value and the formula of the request.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RasterPixelCoordinatesHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RasterPixelCoordinatesHttpTests : IDisposable
{
    private const string OwnerUser = "rasterpixels-owner";
    private const string Org = "rasterpixels-org";
    private const string Ds = "rasterpixels-ds";
    private const string Api = $"/orgs/{Org}/ds/{Ds}";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _factory.DdbWrapper.Setup(d => d.GetEntry(It.IsAny<string>(), "ortho.tif"))
            .Returns(new Entry { Path = "ortho.tif", Type = EntryType.GeoRaster, Hash = "h1", Size = 1 });
        _factory.DdbWrapper.Setup(d => d.GetEntry(It.IsAny<string>(), "thermal.jpg"))
            .Returns(new Entry { Path = "thermal.jpg", Type = EntryType.GeoImage, Hash = "h2", Size = 1 });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<(int Status, string Body, string? MediaType)> GetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.AsUser(OwnerUser);
        using var response = await _client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(),
            response.Content.Headers.ContentType?.MediaType);
    }

    [TestCase("raster-point-value", "&x=1&y=2", "x", "lon, lat")]
    [TestCase("raster-area-stats", "&x0=0&y0=0&x1=5&y1=5", "x0", "bbox")]
    public async Task PixelCoordinatesOnAGeoreferencedRaster_Are400WithTheField(string action, string query,
        string field, string mapParameters)
    {
        var (status, body, mediaType) = await GetAsync($"{Api}/{action}?path=ortho.tif{query}");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        mediaType.ShouldBe("application/problem+json");
        var problem = JsonNode.Parse(body)!.AsObject();
        var message =
            $"Pixel coordinates are for R-JPEG images only: 'ortho.tif' is a georeferenced raster, send {mapParameters} (WGS 84)";
        problem["detail"]!.GetValue<string>().ShouldBe(message);
        problem["errors"]![field]![0]!.GetValue<string>().ShouldBe(message);
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task APixelOfAnRJpeg_ReachesLibddbWithTheNodataValueAndTheFormula()
    {
        RasterAnalysisOptions? seen = null;
        _factory.DdbWrapper.Setup(d => d.GetRasterPointValue(It.Is<string>(p => p.EndsWith("thermal.jpg")), 3, 4,
                It.IsAny<RasterAnalysisOptions?>()))
            .Callback<string, int, int, RasterAnalysisOptions?>((_, _, _, o) => seen = o)
            .Returns("""{"value": 21.5}""");

        var (status, body, _) = await GetAsync($"{Api}/raster-point-value?path=thermal.jpg&x=3&y=4&nodata=-9999&formula=CELSIUS");

        status.ShouldBe(StatusCodes.Status200OK, body);
        JsonNode.Parse(body)!["value"]!.GetValue<double>().ShouldBe(21.5);
        seen.ShouldBe(new RasterAnalysisOptions(-9999, "CELSIUS"));
    }

    [Test]
    public async Task AnAreaOfPixelsOfAnRJpeg_ReachesLibddbWithTheNodataValueAndTheFormula()
    {
        RasterAnalysisOptions? seen = null;
        _factory.DdbWrapper.Setup(d => d.GetRasterAreaStats(It.Is<string>(p => p.EndsWith("thermal.jpg")), 0, 1, 5, 6,
                It.IsAny<RasterAnalysisOptions?>()))
            .Callback<string, int, int, int, int, RasterAnalysisOptions?>((_, _, _, _, _, o) => seen = o)
            .Returns("""{"pixelCount": 36}""");

        var (status, body, _) = await GetAsync($"{Api}/raster-area-stats?path=thermal.jpg&x0=0&y0=1&x1=5&y1=6&nodata=0&formula=KELVIN");

        status.ShouldBe(StatusCodes.Status200OK, body);
        JsonNode.Parse(body)!["pixelCount"]!.GetValue<int>().ShouldBe(36);
        seen.ShouldBe(new RasterAnalysisOptions(0, "KELVIN"));
    }
}
