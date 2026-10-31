using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/>, with member management on: the owner of an
/// organization who is not an administrator manages its members by user name with the members endpoints
/// alone (the list of every user, <c>GET /users</c>, is for administrators). The user name must be the one
/// of an existing user (404 otherwise); a member without the Admin permission and an outsider get 403 before
/// the name is looked up, so they cannot learn which user names exist.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~OrganizationMembersHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class OrganizationMembersHttpTests : IDisposable
{
    private const string Owner = "membershttpowner";
    private const string Member = "membershttpmember";
    private const string Outsider = "membershttpoutsider";
    private const string Org = "membershttp-org";
    private const string UnknownUser = "membershttpnobody";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private int _users;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(new Dictionary<string, string>
        {
            ["AppSettings:EnableOrganizationMemberManagement"] = "true"
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        var member = await _factory.CreateUserAsync(Member);
        await _factory.CreateUserAsync(Outsider);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        context.OrganizationsUsers.Add(new OrganizationUser
        {
            Organization = org,
            OrganizationSlug = Org,
            UserId = member.Id,
            Permissions = OrganizationPermissions.ReadWrite,
            GrantedAt = DateTime.UtcNow,
            GrantedBy = owner.Id
        });
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<string> NewUserAsync()
    {
        var name = $"membershttpuser{++_users}";
        await _factory.CreateUserAsync(name);
        return name;
    }

    [Test]
    public async Task Owner_AddsAUserByName_ChangesAndRemovesIt_WithoutBeingAnAdministrator()
    {
        var user = await NewUserAsync();

        (await AddAsync(Owner, user, OrganizationPermissions.ReadOnly)).Status.ShouldBe(StatusCodes.Status204NoContent);
        (await MembersAsync(Owner)).ShouldContain(m => m.UserName == user && m.Permissions == (int)OrganizationPermissions.ReadOnly);

        using (var update = new HttpRequestMessage(HttpMethod.Put, $"/orgs/{Org}/members/{user}")
               {
                   Content = JsonContent.Create(new { permissions = (int)OrganizationPermissions.ReadWriteDelete })
               })
            ((int)(await _client.SendAsync(update.AsUser(Owner))).StatusCode).ShouldBe(StatusCodes.Status204NoContent);
        (await MembersAsync(Owner)).ShouldContain(m => m.UserName == user && m.Permissions == (int)OrganizationPermissions.ReadWriteDelete);

        using (var remove = new HttpRequestMessage(HttpMethod.Delete, $"/orgs/{Org}/members/{user}"))
            ((int)(await _client.SendAsync(remove.AsUser(Owner))).StatusCode).ShouldBe(StatusCodes.Status204NoContent);
        (await MembersAsync(Owner)).ShouldNotContain(m => m.UserName == user);
    }

    [Test]
    public async Task Owner_AddsAUserNameThatDoesNotExist_Is404NamingIt()
    {
        var (status, problem) = await AddAsync(Owner, UnknownUser);

        status.ShouldBe(StatusCodes.Status404NotFound);
        problem.ShouldNotBeNull()["detail"]!.GetValue<string>().ShouldBe($"User '{UnknownUser}' not found");
    }

    [Test]
    public async Task Owner_AddsWithoutAUserName_Is400()
    {
        var (status, problem) = await AddAsync(Owner, " ");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ShouldNotBeNull()["detail"]!.GetValue<string>().ShouldBe("Missing user name");
    }

    [Test]
    public async Task Owner_AddsAUserWhoIsAlreadyAMember_Is409()
    {
        var (status, _) = await AddAsync(Owner, Member);

        status.ShouldBe(StatusCodes.Status409Conflict);
    }

    [TestCase(Member)]
    [TestCase(Outsider)]
    public async Task AMemberWithoutTheAdminPermission_OrAnOutsider_Is403_ForAnExistingAndAnUnknownName(string caller)
    {
        var user = await NewUserAsync();

        (await AddAsync(caller, user)).Status.ShouldBe(StatusCodes.Status403Forbidden);
        (await AddAsync(caller, UnknownUser)).Status.ShouldBe(StatusCodes.Status403Forbidden);
        (await MembersAsync(Owner)).ShouldNotContain(m => m.UserName == user);
    }

    [Test]
    public async Task Anonymous_Is401()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/members")
        {
            Content = JsonContent.Create(new { userName = Outsider })
        };
        using var response = await _client.SendAsync(request);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status401Unauthorized);
    }

    private async Task<(int Status, JsonObject? Problem)> AddAsync(string caller, string userName,
        OrganizationPermissions permissions = OrganizationPermissions.ReadWrite)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/members")
        {
            Content = JsonContent.Create(new { userName, permissions = (int)permissions })
        };
        using var response = await _client.SendAsync(request.AsUser(caller));
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, body.TrimStart().StartsWith('{') ? JsonNode.Parse(body) as JsonObject : null);
    }

    private async Task<IReadOnlyList<(string UserName, int Permissions)>> MembersAsync(string caller)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/orgs/{Org}/members");
        using var response = await _client.SendAsync(request.AsUser(caller));
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status200OK);
        var members = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        return members.Select(m => (m!["userName"]!.GetValue<string>(), m["permissions"]!.GetValue<int>())).ToList();
    }
}
