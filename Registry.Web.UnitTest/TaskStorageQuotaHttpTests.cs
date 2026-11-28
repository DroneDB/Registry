using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-XC-26, FR-PRC-08, SEC-003, AC-018 through HTTP: a task that adds files to an organization whose owner is over quota is
/// refused at submit with <c>507</c> <c>application/problem+json</c>, whoever submits: a member who is not the owner gets
/// the same answer. The task API runs the one gate (<c>HeavyTaskRunner.CheckSubmitAsync</c>) for every tool that adds files;
/// the archive extract stands for them here, the per-tool behaviour is in the runner tests.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TaskStorageQuotaHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class TaskStorageQuotaHttpTests : IDisposable
{
    private const string Owner = "taskquota-owner";
    private const string Member = "taskquota-member";
    private const string Org = "taskquota-org";
    private const string Ds = "taskquota-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _folder = null!;
    private string _ownerId = null!;
    private readonly OwnerQuota _quota = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(null, services =>
        {
            services.RemoveAll<IStorageQuota>();
            services.AddSingleton<IStorageQuota>(_quota);
        });
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // An empty index: the name rule of the destination folder lists its parent
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.Is<EntryQuery>(q => !q.Recursive)))
            .Returns([]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        _ownerId = owner.Id;
        _quota.OwnerIs(owner.Id);
        var member = await _factory.CreateUserAsync(Member);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        context.OrganizationsUsers.Add(new Registry.Web.Data.Models.OrganizationUser
        {
            Organization = org, OrganizationSlug = Org, UserId = member.Id, Permissions = OrganizationPermissions.ReadWrite
        });
        await context.SaveChangesAsync();

        _folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef).DatasetFolderPath;
        Directory.CreateDirectory(_folder);
        using (var file = File.Create(Path.Combine(_folder, "flight.zip")))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        using (var entry = zip.CreateEntry("a.txt").Open())
            entry.Write(Encoding.UTF8.GetBytes(new string('a', 2000)));
        _factory.DdbWrapper.Setup(d => d.GetEntry(_folder, "flight.zip"))
            .Returns(new Entry { Path = "flight.zip", Type = EntryType.Generic, Size = 300 });
        _factory.DdbWrapper.Setup(d => d.GetEntry(_folder, It.Is<string>(p => p != "flight.zip"))).Returns((Entry?)null);
    }

    [SetUp]
    public void SetUp()
    {
        _quota.Remaining = null;
        _quota.OwnersAsked.Clear();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Problem)> SubmitAsync(string user, string dest = "out")
    {
        var body = new StringContent(
            "{\"toolId\":\"archive-extract\",\"params\":{\"sourcePath\":\"flight.zip\",\"destPath\":\"" + dest + "\"}}",
            Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/tasks") { Content = body }.AsUser(user);
        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response, text.TrimStart().StartsWith('{') ? JsonNode.Parse(text)!.AsObject() : null);
    }

    private static void ShouldBeQuotaProblem(HttpResponseMessage response, JsonObject? problem, string text)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.InsufficientStorage);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        problem.ShouldNotBeNull();
        problem["status"]!.GetValue<int>().ShouldBe(507);
        problem["title"]!.GetValue<string>().ShouldBe("Insufficient Storage");
        problem["detail"]!.GetValue<string>().ShouldBe(text);
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [TestCase(Owner)]
    [TestCase(Member)]
    public async Task Submit_OfAToolThatAddsFiles_ToAnOrganizationWhoseOwnerHasNothingLeft_Is507(string user)
    {
        _quota.Remaining = 0;

        var (response, problem) = await SubmitAsync(user, dest: "none-left-" + user);

        ShouldBeQuotaProblem(response, problem, "Storage quota exceeded: the owner of this organization has 0 B left.");
        _quota.OwnersAsked.Distinct().ShouldBe([_ownerId], "the quota is the owner's, not the submitter's");
    }

    // The archive holds 2000 bytes: the known estimate (uncompressed size) does not fit what the owner has left
    [Test]
    public async Task Submit_WhenTheEstimateOfTheArchiveDoesNotFit_Is507WithWhatIsLeft()
    {
        _quota.Remaining = 1500;

        var (response, problem) = await SubmitAsync(Member, dest: "estimate");

        ShouldBeQuotaProblem(response, problem, "Storage quota exceeded: the owner of this organization has 1.46 KB left.");
    }

    [Test]
    public async Task Submit_WhenTheEstimateFits_IsAccepted()
    {
        _quota.Remaining = 5000;

        var (response, _) = await SubmitAsync(Member, dest: "fits");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Test]
    public async Task Submit_WithoutALimit_IsAccepted()
    {
        _quota.Remaining = null;

        var (response, _) = await SubmitAsync(Owner, dest: "unlimited");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    // FR-XC-26, D-037 (§8 Failed -> retry): the retry of a failed task runs the submit checks again, the storage quota of
    // the owner too: an owner with nothing left gets 507 ProblemDetails, and the task stays failed
    [Test]
    public async Task Retry_OfAFailedTaskThatAddsFiles_ForAnOwnerWithNothingLeft_Is507()
    {
        _quota.Remaining = null;
        var (submitted, body) = await SubmitAsync(Member, dest: "retry-over-quota");
        submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var taskId = body!["taskId"]!.GetValue<string>();

        // The run failed
        var storage = _factory.Services.GetRequiredService<Hangfire.JobStorage>();
        Hangfire.BackgroundJobClientExtensions.ChangeState(new Hangfire.BackgroundJobClient(storage), taskId,
                new Hangfire.States.FailedState(new InvalidOperationException("boom")))
            .ShouldBeTrue();
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            var row = context.JobIndices.Single(j => j.JobId == taskId);
            row.CurrentState = Hangfire.States.FailedState.StateName;
            await context.SaveChangesAsync();
        }

        _quota.Remaining = 0;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/tasks/{taskId}/retry").AsUser(Member);
        var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        ShouldBeQuotaProblem(response, JsonNode.Parse(text)!.AsObject(),
            "Storage quota exceeded: the owner of this organization has 0 B left.");
        using var check = storage.GetConnection();
        check.GetStateData(taskId).Name.ShouldBe(Hangfire.States.FailedState.StateName, "the task was not requeued");
    }

    /// <summary>The quota of owners: what is left (null: no limit) for any owner.</summary>
    private sealed class OwnerQuota : IStorageQuota
    {
        public long? Remaining { get; set; }
        public System.Collections.Concurrent.ConcurrentQueue<string?> OwnersAsked { get; } = new();

        public UserStorageInfo GetUserStorage(Registry.Web.Identity.Models.User user) => new();

        public Task<string?> OwnerIdOfAsync(string orgSlug, CancellationToken ct = default) =>
            Task.FromResult<string?>(orgSlug == Org ? _ownerIdValue : null);

        private string? _ownerIdValue;

        public void OwnerIs(string id) => _ownerIdValue = id;

        public Task<UserStorageInfo?> GetOwnerStorageLimitAsync(string? ownerId, CancellationToken ct = default) =>
            Task.FromResult<UserStorageInfo?>(Remaining is { } r ? new UserStorageInfo { Total = r, Used = 0 } : null);

        public Task<long?> RemainingBytesAsync(string? ownerId, CancellationToken ct = default)
        {
            OwnersAsked.Enqueue(ownerId);
            return Task.FromResult(Remaining);
        }

        public Task CheckOwnerStorageAsync(string? ownerId, long size = 0, CancellationToken ct = default) =>
            Remaining is { } r && size > r
                ? throw Registry.Web.Exceptions.QuotaExceededException.OwnerOverQuota(r)
                : Task.CompletedTask;
    }
}
