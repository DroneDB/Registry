using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Models.DTO;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The wire behaviour of the remote import. Before, <c>POST /sys/import-dataset</c> and
/// <c>/sys/import-organization</c> ran the whole transfer inside the request (hours for a large dataset, cut by
/// any proxy timeout, no cancellation) and answered 200 with the result. Now they validate the request, queue a
/// background job and answer <c>202 {jobId, statusUrl}</c> with <c>Location</c>; <c>GET /sys/imports/{jobId}</c>
/// reports the state and, once done, the same <see cref="ImportResultDto"/>; <c>DELETE</c> cancels. The source
/// password is stored encrypted in the job, and the status route only knows import jobs. All admin only.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~RemoteImportWireTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public sealed class RemoteImportWireTests : IDisposable
{
    private const string RegularUser = "remoteimportwire-import-user";
    private const string Password = "remoteimportwire-secret-password";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(new Dictionary<string, string>
        {
            // The source host passes the SSRF guard without a DNS lookup; nothing is fetched (no job server)
            ["AppSettings:Import:SsrfAllowedHosts:0"] = "remote.test"
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await _factory.CreateUserAsync(RegularUser);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private static object DatasetRequest(string url = "https://remote.test") => new
    {
        sourceRegistryUrl = url,
        username = "remote-user",
        password = Password,
        sourceOrganization = "src",
        sourceDataset = "ds",
        destinationOrganization = "dst",
        mode = ImportMode.ParallelFiles
    };

    private async Task<(HttpResponseMessage Response, JsonNode? Body)> SendAsync(HttpMethod method, string url,
        object? body = null, string user = RegistryWebFactory.AdminUserName)
    {
        using var request = new HttpRequestMessage(method, url)
            .AsUser(user, isAdmin: user == RegistryWebFactory.AdminUserName);
        if (body != null)
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    private async Task<string> StartAsync(string route = "/sys/import-dataset", object? body = null)
    {
        var (response, started) = await SendAsync(HttpMethod.Post, route, body ?? DatasetRequest());
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return started!["jobId"]!.GetValue<string>();
    }

    private async Task<JsonNode> StatusAsync(string jobId)
    {
        var (response, status) = await SendAsync(HttpMethod.Get, $"/sys/imports/{jobId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return status!;
    }

    [Test]
    public async Task ImportDataset_IsQueued_202WithTheJobAndItsStatusUrl()
    {
        var (response, started) = await SendAsync(HttpMethod.Post, "/sys/import-dataset", DatasetRequest());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var jobId = started!["jobId"]!.GetValue<string>();
        var statusUrl = started["statusUrl"]!.GetValue<string>();
        statusUrl.ShouldEndWith($"/sys/imports/{jobId}");
        response.Headers.Location!.ToString().ShouldBe(statusUrl);

        var status = await StatusAsync(jobId);
        status["jobId"]!.GetValue<string>().ShouldBe(jobId);
        status["state"]!.GetValue<string>().ShouldBe(EnqueuedState.StateName);
        status["result"].ShouldBeNull();
    }

    [Test]
    public async Task ImportOrganization_IsQueued_Too()
    {
        var jobId = await StartAsync("/sys/import-organization", new
        {
            sourceRegistryUrl = "https://remote.test",
            sourceOrganization = "src",
            mode = ImportMode.Archive
        });

        (await StatusAsync(jobId))["state"]!.GetValue<string>().ShouldBe(EnqueuedState.StateName);
    }

    [Test]
    public async Task TheJob_IsTheRemoteImport_AndKeepsThePasswordEncrypted()
    {
        var jobId = await StartAsync();

        using var connection = _factory.Services.GetRequiredService<JobStorage>().GetConnection();
        var job = connection.GetJobData(jobId)!.Job;
        job.Type.Name.ShouldBe("RemoteImportJob");
        JsonConvert.SerializeObject(job.Args).ShouldNotContain(Password);
    }

    [Test]
    public async Task Status_OfAFinishedImport_CarriesTheResult_OrTheError()
    {
        var succeeded = await StartAsync();
        var failed = await StartAsync();
        var jobs = _factory.Services.GetRequiredService<IBackgroundJobClient>();
        jobs.ChangeState(succeeded, new SucceededState(new ImportResultDto
        {
            ImportedItems = [new ImportedItemDto { Organization = "dst", Dataset = "ds", FileCount = 3, Size = 12 }],
            Errors = [],
            FileErrors = [],
            TotalFiles = 3,
            TotalSize = 12
        }, 0, 0));
        jobs.ChangeState(failed, new FailedState(new InvalidOperationException("remote unreachable")));

        var done = await StatusAsync(succeeded);
        done["state"]!.GetValue<string>().ShouldBe(SucceededState.StateName);
        done["result"]!["totalFiles"]!.GetValue<int>().ShouldBe(3);
        done["result"]!["importedItems"]!.AsArray().Single()!["dataset"]!.GetValue<string>().ShouldBe("ds");

        var error = await StatusAsync(failed);
        error["state"]!.GetValue<string>().ShouldBe(FailedState.StateName);
        error["error"]!.GetValue<string>().ShouldBe("remote unreachable");
    }

    [Test]
    public async Task Delete_CancelsTheImport()
    {
        var jobId = await StartAsync();

        var (response, _) = await SendAsync(HttpMethod.Delete, $"/sys/imports/{jobId}");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StatusAsync(jobId))["state"]!.GetValue<string>().ShouldBe(DeletedState.StateName);
    }

    [Test]
    public async Task Status_KnowsOnlyImportJobs()
    {
        var other = _factory.Services.GetRequiredService<IBackgroundJobClient>().Enqueue(() => Console.WriteLine("other"));

        (await SendAsync(HttpMethod.Get, $"/sys/imports/{other}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Delete, $"/sys/imports/{other}")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, "/sys/imports/no-such-job")).Response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AnInvalidRequest_IsRefusedBeforeAnyJob()
    {
        (await SendAsync(HttpMethod.Post, "/sys/import-dataset", DatasetRequest("ftp://remote.test")))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SendAsync(HttpMethod.Post, "/sys/import-dataset", new { sourceRegistryUrl = "https://remote.test" }))
            .Response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task NonAdmins_AreRefused()
    {
        var jobId = await StartAsync();

        (await SendAsync(HttpMethod.Post, "/sys/import-dataset", DatasetRequest(), RegularUser))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, $"/sys/imports/{jobId}", user: RegularUser))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Delete, $"/sys/imports/{jobId}", user: RegularUser))
            .Response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
