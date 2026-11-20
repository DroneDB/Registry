using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-PRC-28 through HTTP: <c>GET /sys/tasks</c> sorts by <c>sortField</c> (<c>createdAt</c>, <c>state</c>,
/// <c>toolId</c>) in <c>sortOrder</c> (<c>asc</c>, <c>desc</c>); without them the order is creation, newest first;
/// any other value is a 400 ValidationProblemDetails whose <c>errors</c> names <c>sortField</c> or <c>sortOrder</c>
/// (D-047, FR-XC-11); the paging and the <c>total</c> of the answer are those of the unsorted list.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~AdminTasksSortHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class AdminTasksSortHttpTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient();

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        void Add(string id, string tool, string state, int minutes) =>
            context.JobIndices.Add(new JobIndex
            {
                JobId = id, OrgSlug = "sorthttp", DsSlug = "ds", Path = "p", ToolId = tool, ToolVersion = "1",
                CurrentState = state, CreatedAtUtc = T0.AddMinutes(minutes), LastStateChangeUtc = T0.AddMinutes(minutes)
            });
        Add("s1", "build", "Failed", 10);
        Add("s2", "align", "Succeeded", 20);
        Add("s3", "zip", "Failed", 30);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<(HttpStatusCode Status, string? ContentType, JsonNode Body)> GetAsync(string query)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sys/tasks" + query)
            .AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);
        using var response = await _client.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType,
            JsonNode.Parse(await response.Content.ReadAsStringAsync())!);
    }

    private async Task<string[]> IdsAsync(string query)
    {
        var (status, _, body) = await GetAsync(query);
        status.ShouldBe(HttpStatusCode.OK);
        return [.. body["items"]!.AsArray().Select(i => i!["taskId"]!.GetValue<string>())];
    }

    [Test]
    public async Task WithoutSortParameters_TheOrderIsCreatedAtDescending()
        => (await IdsAsync("")).ShouldBe(["s3", "s2", "s1"]);

    [Test]
    public async Task EmptySortParameters_AreTheDefaults()
        => (await IdsAsync("?sortField=&sortOrder=")).ShouldBe(["s3", "s2", "s1"]);

    [Test]
    public async Task SortByCreatedAtAscending()
        => (await IdsAsync("?sortField=createdAt&sortOrder=asc")).ShouldBe(["s1", "s2", "s3"]);

    [Test]
    public async Task SortByStateAscending_TiesNewestFirst()
        => (await IdsAsync("?sortField=state&sortOrder=asc")).ShouldBe(["s3", "s1", "s2"]);

    [Test]
    public async Task SortByToolIdDescending()
        => (await IdsAsync("?sortField=toolId&sortOrder=desc")).ShouldBe(["s3", "s1", "s2"]);

    [Test]
    public async Task SortFieldAlone_KeepsTheDefaultDirection()
        => (await IdsAsync("?sortField=toolId")).ShouldBe(["s3", "s1", "s2"]);

    [Test]
    public async Task SortKeepsTheTotalAndThePage()
    {
        var (status, _, body) = await GetAsync("?sortField=toolId&sortOrder=asc&skip=1&take=1");
        status.ShouldBe(HttpStatusCode.OK);
        body["total"]!.GetValue<long>().ShouldBe(3);
        body["skip"]!.GetValue<int>().ShouldBe(1);
        body["take"]!.GetValue<int>().ShouldBe(1);
        body["items"]!.AsArray().Select(i => i!["taskId"]!.GetValue<string>()).ShouldBe(["s1"]);
    }

    [Test]
    public async Task AnUnknownSortField_Is400WithErrorsSortField()
    {
        var (status, contentType, body) = await GetAsync("?sortField=progressPercent");

        status.ShouldBe(HttpStatusCode.BadRequest);
        contentType.ShouldBe("application/problem+json");
        body["errors"]!["sortField"]!.AsArray().Count.ShouldBe(1);
        body["errors"]!["sortField"]![0]!.GetValue<string>()
            .ShouldBe("'progressPercent' is not a sortable column. Use one of: createdAt, state, toolId.");
        ((JsonObject)body["errors"]!).ContainsKey("sortOrder").ShouldBeFalse();
    }

    [Test]
    public async Task AnUnknownSortOrder_Is400WithErrorsSortOrder()
    {
        var (status, contentType, body) = await GetAsync("?sortField=state&sortOrder=up");

        status.ShouldBe(HttpStatusCode.BadRequest);
        contentType.ShouldBe("application/problem+json");
        body["errors"]!["sortOrder"]![0]!.GetValue<string>()
            .ShouldBe("'up' is not a sort order. Use one of: asc, desc.");
        ((JsonObject)body["errors"]!).ContainsKey("sortField").ShouldBeFalse();
    }

    [Test]
    public async Task TheApiDescription_NamesTheTwoQueryParameters()
    {
        var document = JsonNode.Parse(await _client.GetStringAsync("/swagger/v1/swagger.json"))!;
        var parameters = document["paths"]!["/sys/tasks"]!["get"]!["parameters"]!.AsArray();

        string Description(string name) =>
            parameters.Single(p => p!["name"]!.GetValue<string>() == name && p["in"]!.GetValue<string>() == "query")!
                ["description"]!.GetValue<string>();

        Description("sortField").ShouldContain("createdAt");
        Description("sortField").ShouldContain("toolId");
        Description("sortOrder").ShouldContain("asc");
        Description("sortOrder").ShouldContain("desc");
    }
}
