using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/>: a dataset that a task imports (or whose index a task rescans) is busy.
/// Every request on it answers 409 with the ProblemDetails members <c>datasetBusy</c> and <c>taskId</c> (noRetry), but for
/// its delete and its tasks (list, status, log, events, cancel); the dataset list says which datasets are busy; and the
/// rescan endpoint queues a <c>rescan-index</c> task (202) that makes the dataset busy.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetBusyHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class DatasetBusyHttpTests : IDisposable
{
    private const string Owner = "datasetbusyowner";
    private const string Org = "datasetbusy-org";
    private const string Busy = "importing-ds";
    private const string Idle = "idle-ds";
    private const string ImportTask = "busy-import";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // An empty dataset: its root entry, nothing indexed
        _factory.DdbWrapper.Setup(d => d.Info(It.IsAny<string>(), false, 0, false))
            .Returns((string path, bool _, int _, bool _) =>
                [new Registry.Ports.DroneDB.Entry { Path = "file://" + path, Type = EntryType.DroneDB, Properties = [] }]);
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.IsAny<EntryQuery>())).Returns([]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id);
        Seed.Dataset(context, org, Busy);
        Seed.Dataset(context, org, Idle);
        await context.SaveChangesAsync();
    }

    [SetUp]
    public async Task SetUp()
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        context.JobIndices.RemoveRange(context.JobIndices);
        context.JobIndices.Add(new JobIndex
        {
            JobId = ImportTask, OrgSlug = Org, DsSlug = Busy, ToolId = HeavyToolIds.ImportDataset, ToolVersion = "1",
            CurrentState = "Processing", ProgressPercent = 37, CreatedAtUtc = DateTime.UtcNow,
            LastStateChangeUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<(int Status, JsonNode? Body)> SendAsync(HttpMethod method, string url, HttpContent? content = null,
        string? user = Owner)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (user != null)
            request.AsUser(user);
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body));
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new System.Collections.Generic.KeyValuePair<string, string>(f.Key, f.Value)));

    private static MultipartFormDataContent Upload()
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("data"), "file", "a.txt");
        form.Add(new StringContent("a.txt"), "path");
        return form;
    }

    private const string Dataset = "/orgs/" + Org + "/ds/" + Busy;

    // The requests a busy dataset refuses: reads, writes, the tasks that would change it, and the dataset by STAC
    private static readonly (string Method, string Url, Func<HttpContent?> Body)[] Refused =
    [
        ("GET", Dataset, () => null),
        ("GET", Dataset + "/ex", () => null),
        ("GET", Dataset + "/list", () => null),
        ("POST", Dataset + "/meta/set/annotations", () => Form(("data", "[]"))),
        ("POST", Dataset + "/obj", Upload),
        ("POST", Dataset + "/tasks", () => Json("""{"toolId":"build"}""")),
        ("POST", Dataset + "/rename", () => Form(("slug", "renamed"))),
        ("POST", Dataset + "/rescan", () => null),
        ("POST", Dataset + "/push/init", () => Form(("checksum", "x"), ("stampJson", "{}"))),
        // A dataset named in the form: the destination of a transfer from another dataset
        ("POST", "/orgs/" + Org + "/ds/" + Idle + "/transfer",
            () => Form(("sourcePath", "a.tif"), ("destOrgSlug", Org), ("destDsSlug", Busy))),
        // A new share batch on it
        ("POST", "/share/init", () => Form(("tag", Org + "/" + Busy))),
        ("GET", "/stac/collections/" + Org + "/" + Busy + "/items/a.tif", () => null)
    ];

    private static System.Collections.Generic.IEnumerable<TestCaseData> RefusedCases() =>
        Refused.Select(r => new TestCaseData(r.Method, r.Url, r.Body).SetName($"ABusyDataset_Refuses {r.Method} {r.Url}"));

    [TestCaseSource(nameof(RefusedCases))]
    public async Task ABusyDataset_Refuses(string method, string url, Func<HttpContent?> body)
    {
        var (status, problem) = await SendAsync(new HttpMethod(method), url, body());

        status.ShouldBe(StatusCodes.Status409Conflict);
        problem.ShouldNotBeNull();
        problem[ApiErrorResponder.DatasetBusyMember]!.GetValue<string>().ShouldBe(DatasetActivity.Importing);
        problem[ApiErrorResponder.TaskIdMember]!.GetValue<string>().ShouldBe(ImportTask);
        problem[ApiErrorResponder.NoRetryMember]!.GetValue<bool>().ShouldBeTrue();
        problem["detail"]!.GetValue<string>()
            .ShouldBe($"Dataset '{Org}/{Busy}' is importing: it can only be deleted until the import ends");
    }

    private static void ShouldBeBusy((int Status, JsonNode? Body) answer)
    {
        var (status, problem) = answer;
        status.ShouldBe(StatusCodes.Status409Conflict);
        problem.ShouldNotBeNull();
        problem[ApiErrorResponder.DatasetBusyMember]!.GetValue<string>().ShouldBe(DatasetActivity.Importing);
        problem[ApiErrorResponder.TaskIdMember]!.GetValue<string>().ShouldBe(ImportTask);
    }

    /// <summary>
    /// A share batch names its dataset by its token: a batch started before the dataset became busy can no longer be
    /// read, uploaded to, committed or rolled back, and nothing reaches the dataset (red before: all of them answered and
    /// the upload and commit wrote into the dataset the import was filling).
    /// </summary>
    [Test]
    public async Task ARunningShareBatch_OfABusyDataset_IsRefused()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            context.JobIndices.RemoveRange(context.JobIndices);
            await context.SaveChangesAsync();
        }

        var (initStatus, init) = await SendAsync(HttpMethod.Post, "/share/init", Form(("tag", Org + "/" + Busy)));
        initStatus.ShouldBe(StatusCodes.Status200OK, init?.ToJsonString());
        var token = init!["token"]!.GetValue<string>();
        await SetUp();

        ShouldBeBusy(await SendAsync(HttpMethod.Get, "/share/info/" + token));
        ShouldBeBusy(await SendAsync(HttpMethod.Post, "/share/upload/" + token, Upload()));
        var chunk = new MultipartFormDataContent
        {
            { new StringContent("file-1"), "fileId" }, { new StringContent("0"), "chunkIndex" },
            { new StringContent("1"), "totalChunks" }, { new StringContent("4"), "totalFileSize" },
            { new StringContent("a.txt"), "path" }, { new StringContent("data"), "file", "a.txt" }
        };
        ShouldBeBusy(await SendAsync(HttpMethod.Post, "/share/upload-chunk/" + token, chunk));
        ShouldBeBusy(await SendAsync(HttpMethod.Post, "/share/commit/" + token));
        ShouldBeBusy(await SendAsync(HttpMethod.Post, "/share/rollback/" + token));

        using (var scope = _factory.Services.CreateScope())
        {
            var batch = await scope.ServiceProvider.GetRequiredService<RegistryContext>().Batches.AsNoTracking()
                .Include(b => b.Entries).SingleAsync(b => b.Token == token);
            batch.Status.ShouldBe(BatchStatus.Running);
            batch.Entries.ShouldBeEmpty();
        }
    }

    /// <summary>The map tiles skip the check (the hot path of every map): a tile of a busy dataset is not refused as busy.</summary>
    [Test]
    public async Task AMapTile_OfABusyDataset_IsNotChecked()
    {
        var (status, problem) = await SendAsync(HttpMethod.Get, Dataset + "/tiles/1/1/1.png?path=ortho.tif");

        status.ShouldNotBe(StatusCodes.Status409Conflict);
        (problem as JsonObject)?.ContainsKey(ApiErrorResponder.DatasetBusyMember).ShouldNotBe(true);
    }

    /// <summary>Only a caller who may read the dataset learns that it is busy: the others get the answer of the access check.</summary>
    [Test]
    public async Task ACallerWhoCannotReadTheDataset_GetsTheAccessAnswer_NotTheBusyOne()
    {
        var (status, problem) = await SendAsync(HttpMethod.Get, Dataset + "/ex", user: null);

        status.ShouldNotBe(StatusCodes.Status409Conflict);
        problem!.AsObject().ContainsKey(ApiErrorResponder.DatasetBusyMember).ShouldBeFalse();
    }

    [Test]
    public async Task TheTasksOfABusyDataset_StillAnswer()
    {
        (await SendAsync(HttpMethod.Get, Dataset + "/tasks")).Status.ShouldBe(StatusCodes.Status200OK);
        (await SendAsync(HttpMethod.Get, Dataset + "/tasks/" + ImportTask)).Status.ShouldBe(StatusCodes.Status200OK);
        (await SendAsync(HttpMethod.Get, Dataset + "/tasks/" + ImportTask + "/log")).Status.ShouldBe(StatusCodes.Status200OK);

        using (var request = new HttpRequestMessage(HttpMethod.Get, Dataset + "/tasks/events").AsUser(Owner))
        using (var events = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            events.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            events.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        }

        // Cancelled: the dataset is no longer busy (the gate lets the request through)
        (await SendAsync(HttpMethod.Delete, Dataset + "/tasks/" + ImportTask)).Status.ShouldBe(StatusCodes.Status200OK);
        (await SendAsync(HttpMethod.Get, Dataset + "/ex")).Status.ShouldNotBe(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task TheList_SaysWhichDatasetsAreBusy()
    {
        var (status, list) = await SendAsync(HttpMethod.Get, "/orgs/" + Org + "/ds");

        status.ShouldBe(StatusCodes.Status200OK);
        var datasets = list!.AsArray().ToDictionary(d => d!["slug"]!.GetValue<string>(), d => d!["busy"]);
        datasets[Idle].ShouldBeNull();
        var busy = datasets[Busy].ShouldNotBeNull();
        busy["kind"]!.GetValue<string>().ShouldBe(DatasetActivity.Importing);
        busy["taskId"]!.GetValue<string>().ShouldBe(ImportTask);
        busy["progress"]!.GetValue<int>().ShouldBe(37);
    }

    /// <summary>The rescan is a task: 202 with its id, and the dataset is busy until it ends.</summary>
    [Test]
    public async Task TheRescan_QueuesARescanIndexTask_ThatMakesTheDatasetBusy()
    {
        var (status, task) = await SendAsync(HttpMethod.Post, "/orgs/" + Org + "/ds/" + Idle + "/rescan");

        status.ShouldBe(StatusCodes.Status202Accepted);
        var taskId = task!["taskId"]!.GetValue<string>();
        using (var scope = _factory.Services.CreateScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<RegistryContext>().JobIndices.AsNoTracking()
                .SingleAsync(j => j.JobId == taskId);
            row.ToolId.ShouldBe(HeavyToolIds.RescanIndex);
            row.DsSlug.ShouldBe(Idle);
        }

        var (busyStatus, problem) = await SendAsync(HttpMethod.Get, "/orgs/" + Org + "/ds/" + Idle + "/ex");
        busyStatus.ShouldBe(StatusCodes.Status409Conflict);
        problem![ApiErrorResponder.DatasetBusyMember]!.GetValue<string>().ShouldBe(DatasetActivity.Rescanning);
        problem[ApiErrorResponder.TaskIdMember]!.GetValue<string>().ShouldBe(taskId);
    }

    /// <summary>The delete of a busy dataset goes through, and stops the import at once.</summary>
    [Test]
    public async Task ABusyDataset_CanBeDeleted_AndItsImportStops()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            var org = await context.Organizations.SingleAsync(o => o.Slug == Org);
            Seed.Dataset(context, org, "doomed-ds");
            context.JobIndices.Add(new JobIndex
            {
                JobId = "doomed-import", OrgSlug = Org, DsSlug = "doomed-ds", ToolId = HeavyToolIds.ImportDataset,
                ToolVersion = "1", CurrentState = "Enqueued", CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        (await SendAsync(HttpMethod.Delete, "/orgs/" + Org + "/ds/doomed-ds")).Status.ShouldBe(StatusCodes.Status204NoContent);

        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            (await context.Datasets.AnyAsync(d => d.Slug == "doomed-ds")).ShouldBeFalse();
            var row = await context.JobIndices.AsNoTracking().SingleAsync(j => j.JobId == "doomed-import");
            row.CurrentState.ShouldBe("Deleted", "the import is stopped by the delete, before its cleanup runs");
            row.DsSlug.ShouldStartWith(Registry.Web.Data.SlugKeyedRows.TombstonePrefix);
        }
    }
}
