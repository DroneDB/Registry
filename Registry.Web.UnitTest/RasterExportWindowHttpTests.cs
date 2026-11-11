using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Test.Baselines;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>The raster of the fixtures and the submit of its export.</summary>
public abstract class RasterExportWindowFixture : DatasetAccessFixture
{
    protected const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;
    protected const string Raster = "ortho.tif";

    // The value info of the raster: 400,000,000 pixels, 300,000,000 with a value
    protected const string WholeInfo = """{"pixelCount": 400000000, "validPixelCount": 300000000}""";

    [OneTimeSetUp]
    public async Task IndexTheRaster()
    {
        var internalRef = await WithContextAsync(context =>
            Task.FromResult(context.Datasets.Single(d => d.Slug == PrivateDs).InternalRef));
        using var scope = Factory.Services.CreateScope();
        var folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(PrivateOrg, internalRef).DatasetFolderPath;
        Factory.DdbWrapper.Setup(d => d.GetEntry(folder, Raster))
            .Returns(new Entry { Path = Raster, Hash = "h", Type = EntryType.GeoRaster, Size = 1 });
    }

    protected Task<(HttpResponseMessage Response, string Body, JsonObject? Json)> Submit(string paramsJson) =>
        SendAsync(Request(HttpMethod.Post, Ds + "/tasks", Caller.Owner, Json(
            $$"""{"toolId":"raster-export","path":"{{Raster}}","params":{{paramsJson}},"force":true}""")));

    protected async Task<bool?> RasterExportWindowFeature()
    {
        var (response, body, json) = await SendAsync(Request(HttpMethod.Get, "/sys/features", Caller.Anonymous));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        return json!["rasterExportWindow"]?.GetValue<bool>();
    }
}

/// <summary>
/// FR-RAS-16 (D-023) through the task API (<c>POST /tasks</c>): the features say the window is exported (libddb 2.4, the
/// only libddb the Registry runs), the submit of a <c>raster-export</c> checks the estimate of the export, of the window
/// when there is one, against the export limit and answers a 400 ProblemDetails that carries the estimate and the limit;
/// a window under the limit is queued with its box, a box that is not four numbers or has no pixel of the raster is a 400
/// at <c>errors.bbox</c> at the submit, not a failure on the worker (libddb refuses that export; its value info counts no
/// pixel in the box).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RasterExportWindowHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RasterExportWindowHttpTests : RasterExportWindowFixture
{
    private static readonly double[] Box = [12, 45, 13, 46];

    // The value info of the raster with the window: windowPixelCount source pixels in it
    private void WindowInfo(long windowPixels) =>
        Factory.DdbWrapper.Setup(d => d.GetRasterValueInfo(It.IsAny<string>(), It.IsAny<RasterAnalysisOptions?>()))
            .Returns((string _, RasterAnalysisOptions? options) => options?.Bbox is null
                ? WholeInfo
                : $$"""{"pixelCount": 400000000, "validPixelCount": 300000000, "windowPixelCount": {{windowPixels}}}""");

    [Test]
    public async Task TheFeatures_SayTheWindowIsExported() => (await RasterExportWindowFeature()).ShouldBe(true);

    [Test]
    public async Task AnExportOverTheLimit_Is400AtTheSubmit_WithTheEstimateAndTheLimit()
    {
        Factory.DdbWrapper.Setup(d => d.GetRasterValueInfo(It.IsAny<string>(), It.IsAny<RasterAnalysisOptions?>()))
            .Returns(WholeInfo);

        var (response, body, json) = await Submit("{}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["status"]!.GetValue<int>().ShouldBe(400);
        // 300,000,000 pixels x 4 bytes, and the 1 GiB limit of the server
        json["estimatedExportBytes"]!.GetValue<long>().ShouldBe(1_200_000_000);
        json["exportLimitBytes"]!.GetValue<long>().ShouldBe(1_073_741_824);
        json["detail"]!.GetValue<string>().ShouldContain("exceeds maximum allowed size");
    }

    [Test]
    public async Task ABadWindow_Is400AtTheBboxMember()
    {
        var (response, body, json) = await Submit("""{"bbox":[12,45,13]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["errors"]!["bbox"]![0]!.GetValue<string>().ShouldContain("four finite numbers");
    }

    [Test]
    public async Task AWindowUnderTheLimit_IsQueued_WithTheEstimateOfTheWindow()
    {
        // 1,000,000 pixels x 3/4 with a value x 4 bytes = 3,000,000 bytes, under the 1 GiB limit; the whole raster is over it
        WindowInfo(1_000_000);

        var (response, body, json) = await Submit("""{"bbox":[12,45,13,46]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, body);
        json!["taskId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        Factory.DdbWrapper.Verify(d => d.GetRasterValueInfo(It.IsAny<string>(),
            It.Is<RasterAnalysisOptions?>(o => o != null && o.Bbox != null && o.Bbox.SequenceEqual(Box))), Times.AtLeastOnce());
    }

    [Test]
    public async Task AWindowOverTheLimit_Is400_WithTheEstimateOfTheWindow()
    {
        // 2,000,000,000 x 3/4 x 4 = 6,000,000,000 bytes
        WindowInfo(2_000_000_000);

        var (response, body, json) = await Submit("""{"bbox":[12,45,13,46]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["estimatedExportBytes"]!.GetValue<long>().ShouldBe(6_000_000_000);
        json["exportLimitBytes"]!.GetValue<long>().ShouldBe(1_073_741_824);
    }

    [Test]
    public async Task AWindowOutsideTheRaster_Is400AtTheBboxMember()
    {
        WindowInfo(0);

        var (response, body, json) = await Submit("""{"bbox":[12,45,13,46]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["errors"]!["bbox"]![0]!.GetValue<string>().ShouldBe("The box is outside the raster");
        json["estimatedExportBytes"].ShouldBeNull();
    }
}
