using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Registry.Common;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Test.Baselines;
using Registry.Web.Test.Support;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;

namespace Registry.Web.Test;

/// <summary>
/// FR-RAS-11 through the task API (<c>POST /tasks</c>): a contour request is refused at the submit (400, with the message of
/// <c>POST contours</c>), not queued to fail on the worker; a valid one is accepted as the task <c>raster-contours</c>.
/// The tool needs only read access: a reader cancels the task it started (NFR-001, FR-XC-15), nobody else without write
/// access does, and a submit never reuses the task of another user (FR-RAS-11).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RasterContoursHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class RasterContoursHttpTests : DatasetAccessFixture
{
    private const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;
    private const string Raster = "dem.tif";

    // A second read-only member of the organization
    private const string OtherReaderUser = "datasetaccess-reader-2";

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

    protected override async Task SeedAsync(RegistryContext context)
    {
        var id = (await Factory.CreateUserAsync(OtherReaderUser)).Id;
        context.OrganizationsUsers.Add(new OrganizationUser
        {
            OrganizationSlug = PrivateOrg, UserId = id, Permissions = OrganizationPermissions.ReadOnly
        });
        await context.SaveChangesAsync();
    }

    private static HttpRequestMessage AsOtherReader(HttpMethod method, string url, HttpContent? content = null) =>
        new HttpRequestMessage(method, url) { Content = content }.AsUser(OtherReaderUser);

    private static StringContent Contours(double interval, bool force = false) => Json(
        $$"""{"toolId":"raster-contours","path":"{{Raster}}","params":{"interval":{{interval.ToString(CultureInfo.InvariantCulture)}}},"force":{{(force ? "true" : "false")}}}""");

    // Submits a contour task: the status of the answer and the task id
    private async Task<(HttpStatusCode Status, string TaskId)> SubmitAsync(HttpRequestMessage request)
    {
        var (response, body, json) = await SendAsync(request);
        ((int)response.StatusCode).ShouldBeOneOf([200, 202], body);
        return (response.StatusCode, json!["taskId"]!.GetValue<string>());
    }

    private static string[] ActionsOf(JsonObject? status) =>
        [.. status!["actions"]!.AsArray().Select(a => a!.GetValue<string>())];

    [TestCase("""{}""", "Either 'interval' or 'count' must be specified")]
    [TestCase("""{"interval":0}""", "'interval' must be > 0")]
    [TestCase("""{"interval":5,"minElev":9,"maxElev":3}""", "'minElev' must be less than 'maxElev'")]
    public async Task AContourRequestThatIsNotValid_Is400AtTheSubmit(string parameters, string message)
    {
        var (response, body, _) = await SendAsync(Request(HttpMethod.Post, Ds + "/tasks", Caller.Owner, Json(
            $$"""{"toolId":"raster-contours","path":"{{Raster}}","params":{{parameters}},"force":true}""")));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        body.ShouldContain(message);
    }

    // ---- The cancel of a reader (NFR-001, FR-XC-15)

    [Test]
    public async Task AReader_CancelsTheContourTaskItStarted()
    {
        var (_, taskId) = await SubmitAsync(Request(HttpMethod.Post, Ds + "/tasks", Caller.Reader, Contours(11, force: true)));

        var (_, statusBody, status) = await SendAsync(Request(HttpMethod.Get, Ds + "/tasks/" + taskId, Caller.Reader));
        ActionsOf(status).ShouldBe(["viewLog", "cancel"], statusBody);

        var (response, body, json) = await SendAsync(Request(HttpMethod.Delete, Ds + "/tasks/" + taskId, Caller.Reader));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        json!["deleted"]!.GetValue<bool>().ShouldBeTrue();
        var (_, endedBody, ended) = await SendAsync(Request(HttpMethod.Get, Ds + "/tasks/" + taskId, Caller.Reader));
        ended!["state"]!.GetValue<string>().ShouldBe("Deleted", endedBody);
        ActionsOf(ended).ShouldBe(["viewLog"]);
    }

    [Test]
    public async Task AnotherReader_CannotCancelTheContourTaskOfAReader_TheOwnerOfTheDatasetCan()
    {
        var (_, taskId) = await SubmitAsync(Request(HttpMethod.Post, Ds + "/tasks", Caller.Reader, Contours(12, force: true)));

        var (refused, refusedBody, _) = await SendAsync(AsOtherReader(HttpMethod.Delete, Ds + "/tasks/" + taskId));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, refusedBody);

        var (_, ownerBody, ownerStatus) = await SendAsync(Request(HttpMethod.Get, Ds + "/tasks/" + taskId, Caller.Owner));
        ActionsOf(ownerStatus).ShouldBe(["viewLog", "cancel"], ownerBody);
        var (response, body, json) = await SendAsync(Request(HttpMethod.Delete, Ds + "/tasks/" + taskId, Caller.Owner));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        json!["deleted"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task AReader_CannotCancelItsTaskOfATool_ThatNeedsWriteAccess()
    {
        // A merge started in the reader's name (as if it had lost its write access since): no Cancel for it
        const string taskId = "contours-http-merge";
        await WithContextAsync(async context =>
        {
            context.JobIndices.Add(new JobIndex
            {
                JobId = taskId, OrgSlug = PrivateOrg, DsSlug = PrivateDs, ToolId = "merge-multispectral",
                UserId = UserIds[ReaderUser], CurrentState = "Enqueued", CreatedAtUtc = DateTime.UtcNow,
                LastStateChangeUtc = DateTime.UtcNow
            });
            return await context.SaveChangesAsync();
        });

        var (_, statusBody, status) = await SendAsync(Request(HttpMethod.Get, Ds + "/tasks/" + taskId, Caller.Reader));
        ActionsOf(status).ShouldBe(["viewLog"], statusBody);
        var (response, body, _) = await SendAsync(Request(HttpMethod.Delete, Ds + "/tasks/" + taskId, Caller.Reader));
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body);
    }

    // ---- The deduplication of a read tool is per user (FR-RAS-11)

    [Test]
    public async Task TwoReaders_WithTheSameRequest_GetTwoTasks_TheSameReaderTwice_GetsItsTask()
    {
        var (first, firstId) = await SubmitAsync(Request(HttpMethod.Post, Ds + "/tasks", Caller.Reader, Contours(13)));
        var (again, againId) = await SubmitAsync(Request(HttpMethod.Post, Ds + "/tasks", Caller.Reader, Contours(13)));
        var (other, otherId) = await SubmitAsync(AsOtherReader(HttpMethod.Post, Ds + "/tasks", Contours(13)));

        first.ShouldBe(HttpStatusCode.Accepted);
        again.ShouldBe(HttpStatusCode.OK, "the same reader gets its task again");
        againId.ShouldBe(firstId);
        other.ShouldBe(HttpStatusCode.Accepted, "another reader gets a task of its own");
        otherId.ShouldNotBe(firstId);

        // The other reader reads the task it got
        var (status, body, _) = await SendAsync(AsOtherReader(HttpMethod.Get, Ds + "/tasks/" + otherId));
        status.StatusCode.ShouldBe(HttpStatusCode.OK, body);
    }
}
