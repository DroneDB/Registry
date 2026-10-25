#nullable enable
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Registry.Web.Services.HeavyTasks.Ports;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// <c>TaskToolDto.InputSchema</c> was a System.Text.Json <c>JsonElement</c>, but MVC
/// serializes with Newtonsoft, which wrote the struct's public properties: every tool of
/// <c>GET /orgs/{org}/ds/{ds}/tasks/tools</c> came back with <c>"inputSchema": {"valueKind": 1}</c>
/// instead of its JSON schema. The DTO now carries a <c>JToken</c> (the same choice as
/// <c>SubmitTaskRequestDto.Params</c>), so the schema is written as JSON.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~TaskToolsDtoTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
[NonParallelizable]
public class TaskToolsDtoTests
{
    // The public organization and its default dataset are created on the first start
    private const string ToolsUrl = "/orgs/" + MagicStrings.PublicOrganizationSlug + "/ds/" +
                                    MagicStrings.DefaultDatasetSlug + "/tasks/tools";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();

        // The read-access check resolves the dataset visibility from its ddb metadata (native ddb is mocked)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "visibility", It.IsAny<string?>()))
            .Returns("{\"id\":\"visibility\",\"data\":2,\"mtime\":0}");

        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task GetTools_InputSchema_IsTheToolJsonSchema()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ToolsUrl)
            .AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);

        using var response = await _client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        var tools = JArray.Parse(body);

        var registry = _factory.Services.GetRequiredService<IHeavyToolRegistry>();
        tools.Count.ShouldBe(registry.All.Count);

        foreach (var tool in registry.All)
        {
            var dto = tools.Children<JObject>().Single(t => (string?)t["id"] == tool.Id);
            var expected = JToken.Parse(tool.InputSchema.RootElement.GetRawText());

            dto["inputSchema"].ShouldNotBeNull(tool.Id);
            JToken.DeepEquals(dto["inputSchema"], expected)
                .ShouldBeTrue($"{tool.Id}: inputSchema = {dto["inputSchema"]}");
        }
    }
}
