using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.E2eFixes;

/// <summary>
/// <c>POST /share/init</c> over HTTP (§7.1, FR-UPL-02, D-009): the form fields <c>OrgSlug</c>, <c>DatasetName</c> and
/// <c>Visibility</c> (the Hub sends Private unless the user chose another) reach the dataset: its slug, its name and its
/// visibility meta. A request without a visibility (<c>ddb share</c>, older clients) keeps Public; a visibility that is
/// none of the three answers 400 naming the field.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ShareInitHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class ShareInitHttpTests : IDisposable
{
    private const string OwnerUser = "share-owner";
    private const string Org = "share-org";

    private readonly ConcurrentDictionary<string, JsonNode?> _meta = new();
    private readonly List<(string Key, string Data)> _writes = [];
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string _, string key, string? _) =>
                _meta.TryGetValue(key, out var data)
                    ? new JsonObject { ["id"] = key, ["data"] = data?.DeepClone(), ["mtime"] = 1700000000 }.ToJsonString()
                    : throw new DdbException($"Key not found: {key}"));
        _factory.DdbWrapper.Setup(d => d.MetaSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string _, string key, string data, string? _) =>
            {
                lock (_writes)
                    _writes.Add((key, data));
                // The name is written as plain text, the visibility as a JSON number
                var json = key == "name" ? JsonValue.Create(data).ToJsonString() : data;
                _meta[key] = JsonNode.Parse(json);
                return new Meta { Id = key, Data = TestJson.Parse(json), ModifiedTime = DateTime.UtcNow };
            });
        // The dataset root as libddb describes it (the creation reads it back for the DTO)
        _factory.DdbWrapper.Setup(d => d.Info(It.IsAny<string[]>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Returns(() => [new Entry { Path = "", Type = EntryType.DroneDB, Size = 0 }]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _meta.Clear();
        lock (_writes)
            _writes.Clear();
    }

    private async Task<HttpResponseMessage> InitAsync(Dictionary<string, string> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/share/init")
        {
            Content = new FormUrlEncodedContent(form)
        };
        return await _client.SendAsync(request.AsUser(OwnerUser));
    }

    private async Task<(string Slug, bool CreatedDataset)> BatchOfAsync(string token)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var batch = await context.Batches.AsNoTracking().Include(b => b.Dataset).SingleAsync(b => b.Token == token);
        return (batch.Dataset.Slug, batch.CreatedDataset);
    }

    [TestCase("Private", "0")]
    [TestCase("0", "0")]
    [TestCase("Unlisted", "1")]
    [TestCase("Public", "2")]
    [TestCase("2", "2")]
    public async Task TheVisibilityOfTheForm_IsTheVisibilityOfTheCreatedDataset(string sent, string written)
    {
        using var response = await InitAsync(new Dictionary<string, string>
            { ["OrgSlug"] = Org, ["DatasetName"] = $"Survey {sent}", ["Visibility"] = sent });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var token = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        _writes.ShouldContain(("visibility", written));
        _writes.ShouldContain(("name", $"Survey {sent}"));
        (await BatchOfAsync(token)).ShouldBe(($"survey-{sent.ToLowerInvariant()}", true));
    }

    [Test]
    public async Task WithoutAVisibility_TheDatasetIsPublic_AsForDdbShareAndOlderClients()
    {
        using var response = await InitAsync(new Dictionary<string, string>
            { ["OrgSlug"] = Org, ["DatasetName"] = "Old client" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        _writes.ShouldContain(("visibility", "2"));
    }

    [Test]
    public async Task ASecondInitWithTheSameName_GetsTheSuffixTwo()
    {
        var form = new Dictionary<string, string> { ["OrgSlug"] = Org, ["DatasetName"] = "Twice", ["Visibility"] = "Private" };

        using var first = await InitAsync(form);
        using var second = await InitAsync(form);

        var firstToken = JsonNode.Parse(await first.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        var secondToken = JsonNode.Parse(await second.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        (await BatchOfAsync(firstToken)).Slug.ShouldBe("twice");
        (await BatchOfAsync(secondToken)).Slug.ShouldBe("twice-2");
    }

    [Test]
    public async Task AVisibilityThatIsNoneOfTheThree_AnswersBadRequest_NamingTheField()
    {
        using var response = await InitAsync(new Dictionary<string, string>
            { ["OrgSlug"] = Org, ["DatasetName"] = "Bad", ["Visibility"] = "Secret" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["errors"]!.AsObject();
        errors.Select(e => e.Key).ShouldContain(key => key.Equals("Visibility", StringComparison.OrdinalIgnoreCase));
    }

    // D-035, FR-ORG-05: the slug of a new dataset comes from its name, so a name with no Latin letter or digit is refused,
    // at errors.datasetName (the member of this request), with the message of a request that has no slug field
    [TestCase("!!!")]
    [TestCase("Москва")]
    public async Task ADatasetNameWithoutALatinLetterOrDigit_Is400AtTheDatasetNameMember(string name)
    {
        using var response = await InitAsync(new Dictionary<string, string> { ["OrgSlug"] = Org, ["DatasetName"] = name });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["detail"]!.GetValue<string>().ShouldBe("Use at least one Latin letter (a–z) or digit");
        body["errors"]!["datasetName"]![0]!.GetValue<string>().ShouldBe("Use at least one Latin letter (a–z) or digit");
        _writes.ShouldBeEmpty();
    }

    [Test]
    public async Task AnAccentedDatasetName_GetsATransliteratedSlug_AndALongOneStaysWithinTheLimits()
    {
        using var accented = await InitAsync(new Dictionary<string, string> { ["OrgSlug"] = Org, ["DatasetName"] = "Ørsted Café Ü" });
        accented.StatusCode.ShouldBe(HttpStatusCode.OK, await accented.Content.ReadAsStringAsync());
        var token = JsonNode.Parse(await accented.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        (await BatchOfAsync(token)).Slug.ShouldBe("orsted-cafe-u");

        // 127 letters and a space, then more: the cut at 128 would leave a dash
        using var longName = await InitAsync(new Dictionary<string, string>
            { ["OrgSlug"] = Org, ["DatasetName"] = new string('l', 127) + " tail" });
        longName.StatusCode.ShouldBe(HttpStatusCode.OK, await longName.Content.ReadAsStringAsync());
        var longToken = JsonNode.Parse(await longName.Content.ReadAsStringAsync())!["token"]!.GetValue<string>();
        (await BatchOfAsync(longToken)).Slug.ShouldBe(new string('l', 127));

        using var tooLong = await InitAsync(new Dictionary<string, string>
            { ["OrgSlug"] = Org, ["DatasetName"] = new string('n', 257) });
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
