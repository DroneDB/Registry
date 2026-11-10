using System.Linq;
using System.Net;
using System.Net.Http;
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
/// FR-RAS-10 through the task API (<c>POST /tasks</c>): a raster export whose <c>rescale</c> is not a range libddb can
/// read is refused at the submit with 400 at <c>errors.rescale</c>, not queued to fail on the worker.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RasterExportRescaleHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RasterExportRescaleHttpTests : DatasetAccessFixture
{
    private const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;
    private const string Raster = "ortho.tif";

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

    [TestCase("abc", "The rescale value 'abc' is invalid: use two numbers, min and max, separated by a comma, with min less than max (for example 0,255)")]
    [TestCase("0,1e39", "The rescale value '0,1e39' is invalid: use two numbers, min and max, separated by a comma, with min less than max (for example 0,255)")]
    [TestCase("40,10", "The rescale value '40,10' is invalid: use two numbers, min and max, separated by a comma, with min less than max (for example 0,255)")]
    public async Task ARasterExportWithABadRescale_Is400AtTheRescaleMember(string rescale, string message)
    {
        var (response, body, json) = await SendAsync(Request(HttpMethod.Post, Ds + "/tasks", Caller.Owner, Json(
            $$"""{"toolId":"raster-export","path":"{{Raster}}","params":{"rescale":"{{rescale}}"},"force":true}""")));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        json!["errors"]!["rescale"]![0]!.GetValue<string>().ShouldBe(message);
    }
}
