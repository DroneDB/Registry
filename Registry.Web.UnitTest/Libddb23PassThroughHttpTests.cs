using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Test.Baselines;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The members libddb 2.3 adds to its JSON reach the Hub as libddb writes them, with no DTO of the Registry between that
/// drops one: <c>requiredBands</c> and <c>category</c> of each formula of raster-metadata (U-415), <c>detectedSensorName</c>
/// of raster-info (U-415) and <c>properties.warningCode</c> of an entry (U-408, FR-EXP-18; a DWG on a server without
/// dwg2dxf: "dwg-converter-missing").
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Libddb23PassThroughHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class Libddb23PassThroughHttpTests : DatasetAccessFixture
{
    private const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;
    private const string Image = "multispectral.tif";
    private const string Drawing = "plan.dwg";

    private string _folder = null!;

    [OneTimeSetUp]
    public async Task IndexTheFiles()
    {
        var internalRef = await WithContextAsync(context =>
            Task.FromResult(context.Datasets.Single(d => d.Slug == PrivateDs).InternalRef));
        using var scope = Factory.Services.CreateScope();
        _folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(PrivateOrg, internalRef).DatasetFolderPath;
        Factory.DdbWrapper.Setup(d => d.GetEntry(_folder, Image))
            .Returns(new Entry { Path = Image, Hash = "pass-through-hash", Type = EntryType.GeoImage, Size = 1 });
    }

    [Test]
    public async Task RasterMetadata_KeepsTheRequiredBandsAndTheCategoryOfEachFormula()
    {
        Factory.DdbWrapper.Setup(d => d.GetRasterMetadata(It.IsAny<string>(), null, null)).Returns(
            """{"algorithms":[{"id":"NDVI","name":"Normalized Difference Vegetation Index","disabled":true,"disabledReason":"Requires bands: Red, NIR","requiredBands":["Red","NIR"],"category":"multispectral"}]}""");

        var (response, body, _) = await SendAsync(Request(HttpMethod.Get, $"{Ds}/raster-metadata?path={Image}",
            Caller.Owner));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var ndvi = json.RootElement.GetProperty("algorithms")[0];
        ndvi.GetProperty("requiredBands").EnumerateArray().Select(b => b.GetString()).ShouldBe(["Red", "NIR"]);
        ndvi.GetProperty("category").GetString().ShouldBe("multispectral");
    }

    [Test]
    public async Task RasterInfo_KeepsTheNameOfTheDetectedSensor()
    {
        Factory.DdbWrapper.Setup(d => d.GetRasterInfo(It.IsAny<string>())).Returns(
            """{"bandCount":4,"detectedSensor":"generic-4band","detectedSensorName":"Generic 4-band"}""");

        var (response, body, _) = await SendAsync(Request(HttpMethod.Get, $"{Ds}/raster-info?path={Image}",
            Caller.Owner));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("detectedSensorName").GetString().ShouldBe("Generic 4-band");
    }

    [Test]
    public async Task AnEntry_KeepsTheCodeOfItsWarning()
    {
        var entry = new Entry
        {
            Path = Drawing, Hash = "dwg-hash", Type = EntryType.Vector, Size = 10,
            Properties = new Dictionary<string, JsonElement>
            {
                ["warning"] = JsonSerializer.SerializeToElement("DWG support needs dwg2dxf (LibreDWG)"),
                ["warningCode"] = JsonSerializer.SerializeToElement("dwg-converter-missing")
            }
        };
        Factory.DdbWrapper.Setup(d => d.ListEntries(_folder, It.IsAny<EntryQuery>())).Returns([entry]);

        var (response, body, _) = await SendAsync(Request(HttpMethod.Get, $"{Ds}/list", Caller.Owner));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        var listed = json.RootElement.EnumerateArray().Single(e => e.GetProperty("path").GetString() == Drawing);
        listed.GetProperty("properties").GetProperty("warningCode").GetString().ShouldBe("dwg-converter-missing");
    }
}
