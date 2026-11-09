using System;
using System.Net.Http;
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
using Registry.Web.Services.HeavyTasks;
using Registry.Web.Services.PlatformTasks;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/>, <c>DELETE /orgs/{o}/ds/{d}/tasks/{id}</c>: a task whose write record
/// is committed (it published its result, and may be deleting its inputs) cannot be cancelled, and the answer is the
/// 400 of a cancel of an ended task (D-002, FR-XC-27); the task and its record stay as they are.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TaskCancelHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class TaskCancelHttpTests : IDisposable
{
    private const string Owner = "taskcancelhttpowner";
    private const string Org = "taskcancelhttp-org";
    private const string Ds = "taskcancelhttp-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id);
        Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task SeedTaskAsync(string jobId, string state, string? writeRecord)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        context.JobIndices.Add(new JobIndex
        {
            JobId = jobId, OrgSlug = Org, DsSlug = Ds, ToolId = "merge-multispectral", ToolVersion = "1",
            CurrentState = state, CreatedAtUtc = DateTime.UtcNow, LastStateChangeUtc = DateTime.UtcNow,
            WriteRecordJson = writeRecord
        });
        await context.SaveChangesAsync();
    }

    private async Task<JobIndex> RowAsync(string jobId)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        return await context.JobIndices.AsNoTracking().SingleAsync(j => j.JobId == jobId);
    }

    private async Task<(int Status, JsonObject? Problem)> CancelAsync(string jobId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/orgs/{Org}/ds/{Ds}/tasks/{jobId}");
        using var response = await _client.SendAsync(request.AsUser(Owner));
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject);
    }

    [Test]
    public async Task ACommittedTask_Is400_AndKeepsRunning()
    {
        var committed = new TaskWriteRecord { State = TaskWriteState.Committed }.ToJson();
        await SeedTaskAsync("committed-merge", "Processing", committed);

        var (status, problem) = await CancelAsync("committed-merge");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ShouldNotBeNull()["detail"]!.GetValue<string>().ShouldBe(TaskLifecycle.CommittedMessage);
        var row = await RowAsync("committed-merge");
        row.CurrentState.ShouldBe("Processing");
        row.WriteRecordJson.ShouldBe(committed);
    }

    [Test]
    public async Task AnEndedTask_Is400_Too()
    {
        await SeedTaskAsync("ended-merge", "Succeeded", new TaskWriteRecord { State = TaskWriteState.Committed }.ToJson());

        var (status, problem) = await CancelAsync("ended-merge");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ShouldNotBeNull()["detail"]!.GetValue<string>().ShouldContain("already ended");
        (await RowAsync("ended-merge")).CurrentState.ShouldBe("Succeeded");
    }
}
