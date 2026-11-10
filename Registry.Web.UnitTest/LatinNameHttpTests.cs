using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-ORG-05 (D-035) and FR-ORG-14 through HTTP: a create is refused with 400 and the message of D-035 at
/// <c>errors.name</c> when neither the name nor the slug of the request has a Latin letter or a digit; a name in another
/// script is accepted with a slug the user enters. An edit (and the meta <c>name</c>) does not check a name it keeps, and
/// a new name has the slug of the organization or dataset, so an organization named 'Москва' (slug 'moskva') stays
/// editable and may be renamed to '東京'. A name over 256 characters and a description over 2000 are refused at their own
/// field (<c>errors.name</c>, <c>errors.description</c>: the request members in camelCase).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~LatinNameHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class LatinNameHttpTests : IDisposable
{
    private const string User = "latin-name-user";
    private const string WithSlug = "Use at least one Latin letter (a–z) or digit in the name, or enter a slug";
    private const string WithoutSlug = "Use at least one Latin letter (a–z) or digit";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly List<(string Key, string Data)> _metaWrites = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _factory.DdbWrapper.Setup(d => d.MetaSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string _, string key, string data, string? _) =>
            {
                lock (_metaWrites)
                    _metaWrites.Add((key, data));
                return new Meta { Id = key, Data = TestJson.Parse("null"), ModifiedTime = DateTime.UtcNow };
            });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await _factory.CreateUserAsync(User);

        using var org = await SendAsync(HttpMethod.Post, "/orgs", new() { ["slug"] = "latinorg", ["name"] = "Latin org" });
        org.StatusCode.ShouldBe(HttpStatusCode.Created);

        // A dataset that exists, for the meta name
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        Seed.Dataset(context, context.Organizations.Single(o => o.Slug == "latinorg"), "named");
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Dictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(method, url).AsUser(User);
        if (form != null)
            request.Content = new FormUrlEncodedContent(form);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonNode> BodyOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    private static string ErrorAt(JsonNode body, string field) => body["errors"]![field]![0]!.GetValue<string>();

    private int StoredOrganizations(string? slug = null)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<RegistryContext>().Organizations
            .Count(o => slug == null || o.Slug == slug);
    }

    [TestCase("!!!", null)]
    [TestCase("Москва", null)]
    [TestCase("!!!", "")]
    [TestCase("!!!", "!!!")]
    public async Task CreateOrganization_WithANameWithoutALatinLetterOrDigit_AndNoSlug_Is400WithTheMessageOfD035(string name, string? slug)
    {
        var form = new Dictionary<string, string> { ["name"] = name };
        if (slug != null) form["slug"] = slug;
        var before = StoredOrganizations();

        using var response = await SendAsync(HttpMethod.Post, "/orgs", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await BodyOf(response);
        body["detail"]!.GetValue<string>().ShouldBe(WithSlug);
        ErrorAt(body, "name").ShouldBe(WithSlug);
        StoredOrganizations().ShouldBe(before);
    }

    [Test]
    public async Task CreateOrganization_NamedInCyrillic_WithTheSlugTheUserEnters_Is201()
    {
        using var response = await SendAsync(HttpMethod.Post, "/orgs", new() { ["name"] = "Москва", ["slug"] = "moskva" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        StoredOrganizations("moskva").ShouldBe(1);
    }

    // FR-ORG-04, D-047: what must be unique is the slug, so a duplicate organization is a 409 at errors.slug
    [Test]
    public async Task CreateOrganization_WithASlugThatExists_Is409AtTheSlugMember()
    {
        using var first = await SendAsync(HttpMethod.Post, "/orgs", new() { ["name"] = "Survey Team", ["slug"] = "survey-team" });
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var second = await SendAsync(HttpMethod.Post, "/orgs", new() { ["name"] = "Another name", ["slug"] = "survey-team" });

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await BodyOf(second);
        body["detail"]!.GetValue<string>().ShouldBe("The organization already exists");
        ErrorAt(body, "slug").ShouldBe("The organization already exists");
    }

    // The coordinator's decision on D-035: a name the edit keeps is not checked again
    [Test]
    public async Task EditOrganization_NamedInCyrillic_KeepsItsDescriptionAndVisibilityEditable()
    {
        using var created = await SendAsync(HttpMethod.Post, "/orgs", new() { ["name"] = "Москва", ["slug"] = "moskva-edit" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await SendAsync(HttpMethod.Put, "/orgs/moskva-edit",
            new() { ["name"] = "Москва", ["description"] = "The capital", ["isPublic"] = "true" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        var read = await BodyOf(await SendAsync(HttpMethod.Get, "/orgs/moskva-edit"));
        read["name"]!.GetValue<string>().ShouldBe("Москва");
        read["description"]!.GetValue<string>().ShouldBe("The capital");
        read["isPublic"]!.GetValue<bool>().ShouldBeTrue();
    }

    // A new name has the slug of the organization, which the edit keeps: "accepted when a slug is given" (D-035)
    [TestCase("東京")]
    [TestCase("!!!")]
    public async Task EditOrganization_ToANameWithoutALatinLetterOrDigit_IsAccepted_ForTheOrganizationHasASlug(string name)
    {
        var slug = "rename-" + (name == "!!!" ? "signs" : "tokyo");
        using var created = await SendAsync(HttpMethod.Post, "/orgs", new() { ["name"] = "Москва", ["slug"] = slug });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await SendAsync(HttpMethod.Put, "/orgs/" + slug, new() { ["name"] = name });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
        (await BodyOf(await SendAsync(HttpMethod.Get, "/orgs/" + slug)))["name"]!.GetValue<string>().ShouldBe(name);
    }

    [Test]
    public async Task EditOrganization_ToAnAccentedName_Works()
    {
        using var created = await SendAsync(HttpMethod.Post, "/orgs", new() { ["name"] = "Accents", ["slug"] = "accents" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await SendAsync(HttpMethod.Put, "/orgs/accents", new() { ["name"] = "Équipe Été" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [TestCase("!!!", null)]
    [TestCase("Москва", null)]
    [TestCase("!!!", "!!!")]
    public async Task CreateDataset_WithANameWithoutALatinLetterOrDigit_AndNoSlug_Is400WithTheMessageOfD035(string name, string? slug)
    {
        var form = new Dictionary<string, string> { ["name"] = name };
        if (slug != null) form["slug"] = slug;

        using var response = await SendAsync(HttpMethod.Post, "/orgs/latinorg/ds", form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await BodyOf(response);
        body["detail"]!.GetValue<string>().ShouldBe(WithSlug);
        ErrorAt(body, "name").ShouldBe(WithSlug);
    }

    // The dataset keeps its slug: a name in another script is accepted (D-035, the coordinator's decision), and the
    // visibility of a dataset so named can be changed
    [Test]
    public async Task EditDataset_ToANameInAnotherScript_AndItsVisibility_Works()
    {
        lock (_metaWrites)
            _metaWrites.Clear();

        using var renamed = await SendAsync(HttpMethod.Put, "/orgs/latinorg/ds/named", new() { ["name"] = "東京" });
        renamed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await renamed.Content.ReadAsStringAsync());

        using var visibility = await SendAsync(HttpMethod.Put, "/orgs/latinorg/ds/named", new() { ["visibility"] = "2" });
        visibility.StatusCode.ShouldBe(HttpStatusCode.NoContent, await visibility.Content.ReadAsStringAsync());

        lock (_metaWrites)
        {
            _metaWrites.ShouldContain(write => write.Key == "name" && write.Data.Contains("東京"));
            _metaWrites.ShouldContain(write => write.Key == "visibility" && write.Data == "2");
        }
    }

    // The meta key "name" is the display name of the dataset: the same rule (FR-EXP-24), as the Hub sends it (JSON) or
    // as plain text; the dataset has a slug, so a name in another script is accepted
    [TestCase("\"!!!\"")]
    [TestCase("\"Москва\"")]
    [TestCase("Москва")]
    public async Task MetaSetName_WithoutALatinLetterOrDigit_IsAccepted_ForTheDatasetHasASlug(string data)
    {
        using var response = await SendAsync(HttpMethod.Post, "/orgs/latinorg/ds/named/meta/set",
            new() { ["key"] = "name", ["data"] = data });

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task MetaSetName_OfMoreThan256Characters_Is400()
    {
        using var response = await SendAsync(HttpMethod.Post, "/orgs/latinorg/ds/named/meta/set",
            new() { ["key"] = "name", ["data"] = "\"" + new string('n', 257) + "\"" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ErrorAt(await BodyOf(response), "name").ShouldBe("The name is too long: use 1 to 256 characters");
    }

    // FR-ORG-14: the name of an import is a dataset name, with the same limit, refused at errors.name (a JSON body: the
    // key is the request member in camelCase, as for a form)
    [Test]
    public async Task CreateImport_WithANameOver256Characters_Is400AtTheNameMember()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orgs/latinorg/import")
        {
            Content = new StringContent(
                $$"""{"sourceType":"registry","params":{},"name":"{{new string('n', 257)}}"}""",
                System.Text.Encoding.UTF8, "application/json")
        }.AsUser(User);

        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await BodyOf(response);
        ErrorAt(body, "name").ShouldBe("The name is too long: use 1 to 256 characters");
        body["errors"]!.AsObject().Select(member => member.Key).ShouldBe(["name"]);
    }

    [Test]
    public async Task CreateOrganization_WithANameOver256Characters_OrADescriptionOver2000_Is400AtThatField()
    {
        using var longName = await SendAsync(HttpMethod.Post, "/orgs",
            new() { ["name"] = new string('n', 257), ["slug"] = "longname" });
        longName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ErrorAt(await BodyOf(longName), "name").ShouldBe("The name is too long: use 1 to 256 characters");

        using var longDescription = await SendAsync(HttpMethod.Post, "/orgs",
            new() { ["name"] = "Long description", ["slug"] = "longdescription", ["description"] = new string('d', 2001) });
        longDescription.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ErrorAt(await BodyOf(longDescription), "description").ShouldBe("The description is too long: use up to 2000 characters");

        using var limits = await SendAsync(HttpMethod.Post, "/orgs",
            new() { ["name"] = new string('n', 256), ["slug"] = "atlimits", ["description"] = new string('d', 2000) });
        limits.StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
