using System.Threading.Tasks;
using Moq;
using NUnit.Framework;
using Registry.Web.Exceptions;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The argument checks of the raster services: written once, in the service, with the messages the
/// controllers sent, and made before the access check (an invalid request is a 400 whoever sends it).
/// </summary>
[TestFixture]
public class RasterServicesArgumentTests
{
    private Mock<IAuthManager> _auth = null!;
    private ObjectServices _services = null!;

    [SetUp]
    public void SetUp()
    {
        // Strict: any access check fails the test (the checks come first)
        _auth = new Mock<IAuthManager>(MockBehavior.Strict);
        _services = new ObjectServicesBuilder(TestDb.CreateContext<Registry.Web.Data.RegistryContext>())
            .WithAuthManager(_auth.Object)
            .Build();
    }

    private static async Task ShouldBe400(Task call, string message) =>
        (await Should.ThrowAsync<BadRequestException>(call)).Message.ShouldBe(message);

    [Test]
    public async Task Analysis_RefusesInvalidArguments_BeforeTheAccessCheck()
    {
        var raster = _services.Raster;
        await ShouldBe400(raster.GetRasterProfileAsync("o", "d", "a.tif", " ", 10), "lineString (GeoJSON LineString) is required");
        await ShouldBe400(raster.CalculateVolumeAsync("o", "d", "a.tif", "", null, 0), "polygon (GeoJSON Polygon) is required");
        await ShouldBe400(raster.DetectStockpileAsync("o", "d", "a.tif", 1, 2, 0, 0.5f), "radius must be positive");
        await ShouldBe400(raster.DetectStockpileAsync("o", "d", "a.tif", 1, 2, double.NaN, 0.5f), "radius must be positive");
        await ShouldBe400(raster.DetectAllStockpilesAsync("o", "d", "a.tif", 0.5f, -1, 10), "minAreaM2 must be >= 0");
        await ShouldBe400(raster.DetectAllStockpilesAsync("o", "d", "a.tif", 0.5f, 1, 0), "maxResults must be > 0");
    }

    [TestCase(null, null, null, null, 0.0, "Either 'interval' or 'count' must be specified")]
    [TestCase(0.0, null, null, null, 0.0, "'interval' must be > 0")]
    [TestCase(null, 0, null, null, 0.0, "'count' must be > 0")]
    [TestCase(1.0, null, 10.0, 10.0, 0.0, "'minElev' must be less than 'maxElev'")]
    [TestCase(1.0, null, null, null, -1.0, "'simplifyTolerance' must be >= 0")]
    public async Task Contours_RefuseInvalidOptions_BeforeTheAccessCheck(double? interval, int? count,
        double? minElev, double? maxElev, double tolerance, string message) =>
        await ShouldBe400(_services.Raster.GenerateContoursAsync("o", "d", "dem.tif",
            new ContourOptions(interval, count, 0, minElev, maxElev, tolerance, 1)), message);

    [Test]
    public async Task Processing_RequiresItsArguments_BeforeTheAccessCheck()
    {
        var processing = _services.Processing;
        await ShouldBe400(processing.ValidateMergeMultispectralAsync("o", "d", []), "paths is required");
        await ShouldBe400(processing.PreviewMergeMultispectralAsync("o", "d", []), "paths is required");
        await ShouldBe400(processing.MergeMultispectralAsync("o", "d", ["a.tif"], " "), "outputPath is required");
        await ShouldBe400(processing.MergeMultispectralAsync("o", "d", [], "out.tif"), "paths is required");
        await ShouldBe400(processing.ValidateAlignRasterAsync("o", "d", "", "b.tif"), "sourcePath is required");
        await ShouldBe400(processing.ValidateAlignRasterAsync("o", "d", "a.tif", null), "referencePath is required");
    }
}
