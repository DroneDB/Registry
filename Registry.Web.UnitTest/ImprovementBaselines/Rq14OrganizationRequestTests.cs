#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Controllers;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-14: a request type is never a response type. <c>OrganizationDto</c> was the <c>[FromForm]</c> input of
/// <c>POST /orgs</c> and <c>PUT /orgs/{org}</c> and the output of five endpoints, so a request could bind
/// <c>Permissions</c> (the caller's effective permissions) and <c>CreationDate</c>, and the response type's
/// rules were the request's. The organization request type carries what a client may send; <c>Owner</c> stays
/// on purpose: an administrator creates or edits an organization for another user, anybody else naming another
/// owner is refused (403), and the Hub sends none.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq14OrganizationRequestTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class Rq14OrganizationRequestTests : IDisposable
{
    private const string User = "rq14-user";
    private const string Other = "rq14-other";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _otherId = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await _factory.CreateUserAsync(User);
        _otherId = (await _factory.CreateUserAsync(Other)).Id;
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Test]
    public void ARequestType_IsNeverAResponseType()
    {
        var actions = typeof(OrganizationsController).Assembly.GetExportedTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .ToList();

        var requests = actions.SelectMany(a => a.GetParameters())
            .Where(p => p.GetCustomAttributes().Any(a => a is FromBodyAttribute or FromFormAttribute))
            .Select(p => Unwrap(p.ParameterType)).Where(IsRegistryModel).ToHashSet();
        var responses = actions.SelectMany(a => a.GetCustomAttributes<ProducesResponseTypeAttribute>())
            .Select(p => Unwrap(p.Type)).Where(IsRegistryModel).ToHashSet();

        requests.Intersect(responses).Select(t => t.Name).ShouldBeEmpty();
    }

    private static bool IsRegistryModel(Type type) => type.Namespace?.StartsWith("Registry.", StringComparison.Ordinal) == true;

    [Test]
    public void TheRequestType_HasNoMemberOfTheResponseThatAClientDoesNotSet()
    {
        var members = typeof(Registry.Web.Models.DTO.OrganizationRequestDto).GetProperties().Select(p => p.Name);

        members.ShouldBe(["Slug", "Name", "Description", "Owner", "IsPublic"], ignoreOrder: true);
        members.ShouldNotContain("Permissions");
        members.ShouldNotContain("CreationDate");
    }

    private static Type Unwrap(Type type) =>
        type.IsArray ? Unwrap(type.GetElementType()!) :
        type.IsGenericType ? Unwrap(type.GetGenericArguments()[^1]) : type;

    [Test]
    public async Task Create_BindsTheRequestMembers_NotTheOnesOfTheResponse()
    {
        using var response = await Send(HttpMethod.Post, "/orgs", User, new Dictionary<string, string>
        {
            ["slug"] = "rq14-created", ["name"] = "Created", ["description"] = "d", ["isPublic"] = "true",
            // members of the response, sent by a hostile client
            ["permissions.canRead"] = "false", ["permissions.canManageMembers"] = "true",
            ["creationDate"] = "2001-01-01T00:00:00Z"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["slug"]!.GetValue<string>().ShouldBe("rq14-created");
        body["isPublic"]!.GetValue<bool>().ShouldBeTrue();
        body["owner"]!.GetValue<string>().ShouldBe(User);
        body["creationDate"]!.GetValue<DateTime>().ShouldBeGreaterThan(new DateTime(2020, 1, 1));

        // Stored: created now, owned by the caller
        using var scope = _factory.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<RegistryContext>().Organizations.Single(o => o.Slug == "rq14-created");
        stored.CreationDate.ShouldBeGreaterThan(new DateTime(2020, 1, 1));
    }

    [Test]
    public async Task Create_AsAUser_NamingAnotherOwner_IsRefused()
    {
        using var response = await Send(HttpMethod.Post, "/orgs", User, new Dictionary<string, string>
        {
            ["slug"] = "rq14-stolen", ["name"] = "Stolen", ["owner"] = _otherId
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Create_AsAnAdministrator_MayNameTheOwner()
    {
        using var response = await Send(HttpMethod.Post, "/orgs", RegistryWebFactory.AdminUserName, new Dictionary<string, string>
        {
            ["slug"] = "rq14-for-other", ["name"] = "For other", ["owner"] = _otherId
        }, admin: true);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["owner"]!.GetValue<string>().ShouldBe(Other);
    }

    [Test]
    public async Task Update_AsTheOwner_CannotHandTheOrganizationToAnotherUser()
    {
        using var created = await Send(HttpMethod.Post, "/orgs", User,
            new Dictionary<string, string> { ["slug"] = "rq14-update", ["name"] = "Update" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var response = await Send(HttpMethod.Put, "/orgs/rq14-update", User, new Dictionary<string, string>
        {
            ["slug"] = "rq14-update", ["name"] = "Renamed", ["owner"] = _otherId, ["creationDate"] = "2001-01-01T00:00:00Z"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Update_ChangesTheRequestMembers_AndLeavesTheCreationDate()
    {
        using var created = await Send(HttpMethod.Post, "/orgs", User,
            new Dictionary<string, string> { ["slug"] = "rq14-rename", ["name"] = "Before" });
        var creationDate = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["creationDate"]!.GetValue<DateTime>();

        using var response = await Send(HttpMethod.Put, "/orgs/rq14-rename", User, new Dictionary<string, string>
        {
            ["slug"] = "rq14-rename", ["name"] = "After", ["description"] = "d2", ["isPublic"] = "true",
            ["creationDate"] = "2001-01-01T00:00:00Z", ["permissions.canDelete"] = "true"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var scope = _factory.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<RegistryContext>().Organizations.Single(o => o.Slug == "rq14-rename");
        stored.Name.ShouldBe("After");
        stored.Description.ShouldBe("d2");
        stored.IsPublic.ShouldBeTrue();
        stored.CreationDate.ShouldBe(creationDate, TimeSpan.FromSeconds(1));
    }

    [Test]
    public async Task Create_ATypoedMember_IsIgnored_AndTheAnswerShowsWhatWasStored()
    {
        // Unknown request members are ignored (RQ-14 decision: the Hub, the CLI and scripts send more than the DTOs read);
        // the answer names the stored value, so a typo is visible to the caller
        using var response = await Send(HttpMethod.Post, "/orgs", User, new Dictionary<string, string>
        {
            ["slug"] = "rq14-typo", ["name"] = "Typo", ["public"] = "true"
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["isPublic"]!.GetValue<bool>().ShouldBeFalse();
    }

    [TestCase("slug", "")]
    [TestCase("name", "")]
    public async Task Create_WithoutARequiredMember_Is400(string member, string value)
    {
        var form = new Dictionary<string, string> { ["slug"] = "rq14-required", ["name"] = "Required" };
        form[member] = value;

        using var response = await Send(HttpMethod.Post, "/orgs", User, form);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string url, string user,
        Dictionary<string, string> form, bool admin = false)
    {
        using var request = new HttpRequestMessage(method, url) { Content = new FormUrlEncodedContent(form) };
        return await _client.SendAsync(request.AsUser(user, admin));
    }
}
