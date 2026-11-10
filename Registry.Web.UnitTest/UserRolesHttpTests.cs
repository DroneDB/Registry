using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Services.Initialization;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-ADM-04, D-043 and the e-mail rule of FR-XC-12 through HTTP: <c>/sys/features</c> lists the input rules and the
/// system roles; the system roles cannot be deleted; <c>POST /users/{userName}/disable</c> blocks sign-in and access at
/// once and <c>/enable</c> restores them; Edit User keeps <c>deactivated</c> and refuses a mistyped role or e-mail
/// before it changes anything; the system roles are seeded whatever the identity provider.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserRolesHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class UserRolesHttpTests : IDisposable
{
    private const string Admin = RegistryWebFactory.AdminUserName;
    private const string Password = "Password1234";
    private static readonly string[] DeactivatedOnly = [ApplicationDbContext.DeactivatedRoleName];

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

        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "editor", "viewer" })
            if (!await roles.RoleExistsAsync(role))
                (await roles.CreateAsync(new IdentityRole(role))).Succeeded.ShouldBeTrue();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? asUser = Admin,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (asUser != null)
            request.AsUser(asUser, isAdmin: asUser == Admin);
        return await _client.SendAsync(request);
    }

    private async Task<HttpStatusCode> SignInAsync(string userName)
    {
        using var response = await SendAsync(HttpMethod.Post, "/users/authenticate", asUser: null,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = userName, ["password"] = Password }));
        return response.StatusCode;
    }

    private async Task<string[]> RolesOf(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return [.. (await users.GetRolesAsync((await users.FindByNameAsync(userName))!)).OrderBy(r => r)];
    }

    private async Task<User> FindUser(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByNameAsync(userName))!;
    }

    // The body of Edit User
    private static JsonContent Edit(string email, params string[] roles) => JsonContent.Create(new { email, roles });

    private static async Task<string> DetailOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["detail"]!.GetValue<string>();

    private async Task AddRole(string userName, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        (await users.AddToRoleAsync((await users.FindByNameAsync(userName))!, role)).Succeeded.ShouldBeTrue();
    }

    [Test]
    public async Task Features_ListTheInputRules_AndBothSystemRoles()
    {
        using var response = await SendAsync(HttpMethod.Get, "/sys/features", asUser: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["systemRoles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["admin", "deactivated"]);
        var rules = body["inputRules"]!;
        rules["slug"]!["pattern"]!.GetValue<string>().ShouldBe(InputRules.SlugPattern);
        rules["slug"]!["maxLength"]!.GetValue<int>().ShouldBe(SlugRules.MaxLength);
        rules["email"]!["pattern"]!.GetValue<string>().ShouldBe(InputRules.EmailPattern);
        rules["entryName"]!["rootReservedNames"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe([".uploads"]);
        rules["nameClash"]!["caseInsensitive"]!.GetValue<bool>().ShouldBeTrue();
        rules["cron"]!["disableWords"]!.AsArray().Count.ShouldBe(3);
        rules["cron"]!["minFields"]!.GetValue<int>().ShouldBe(5);
        rules["cron"]!["maxFields"]!.GetValue<int>().ShouldBe(6);
        rules["name"]!["pattern"]!.GetValue<string>().ShouldBe("^[a-z0-9]{1,127}$");
        rules["name"]!["minLength"]!.GetValue<int>().ShouldBe(1);
        rules["name"]!["maxLength"]!.GetValue<int>().ShouldBe(127);
        rules["name"]!["description"]!.GetValue<string>().ShouldBe(NameRules.Description);
        rules["latinName"]!["messageWithoutSlug"]!.GetValue<string>().ShouldBe(InputRules.LatinNameMessageWithoutSlug);
    }

    [TestCase("deactivated")]
    [TestCase("admin")]
    [TestCase("Deactivated")]
    public async Task DeleteRole_ASystemRole_Is400(string role)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"/users/roles/{role}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailOf(response)).ShouldContain("system role");
    }

    // FR-ADM-01: a role from before the name rule, with a "/", is listed and can be deleted (the Hub sends it encoded)
    [TestCase("a%2Fb", "a/b")]
    [TestCase("a%2fb%2Fc", "a/b/c")]
    [TestCase("Bad%20Role!", "Bad Role!")]
    public async Task DeleteRole_ALegacyRoleWithASlashOrASign_Is204_AndTheRoleIsGone(string route, string stored)
    {
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>()
                .CreateAsync(new IdentityRole(stored))).Succeeded.ShouldBeTrue();

        using var listed = await SendAsync(HttpMethod.Get, "/users/roles");
        JsonNode.Parse(await listed.Content.ReadAsStringAsync())!.AsArray()
            .Select(r => r!.GetValue<string>()).ShouldContain(stored);

        using var response = await SendAsync(HttpMethod.Delete, $"/users/roles/{route}");

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var scope2 = _factory.Services.CreateScope();
        (await scope2.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().RoleExistsAsync(stored)).ShouldBeFalse();
    }

    [TestCase("a/b")]
    [TestCase("Bad Role!")]
    [TestCase("a-b")]
    public async Task CreateRole_ANameOutsideTheRule_Is400_WithTheRuleAtTheRoleNameField(string name)
    {
        using var response = await SendAsync(HttpMethod.Post, "/users/roles", content: JsonContent.Create(new { roleName = name }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["detail"]!.GetValue<string>().ShouldBe(NameRules.InvalidMessage("Role name"));
        body["errors"]!["roleName"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe([NameRules.InvalidMessage("Role name")]);
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>().RoleExistsAsync(name)).ShouldBeFalse();
    }

    // FR-ADM-02, FR-ADM-22: the refusal of Add User names its field
    [TestCase("t-admin-user")]
    [TestCase("TADMIN")]
    [TestCase("bad name!")]
    public async Task CreateUser_ANameOutsideTheRule_Is400_WithTheRuleAtTheUserNameField(string name)
    {
        using var response = await SendAsync(HttpMethod.Post, "/users", content: JsonContent.Create(
            new { userName = name, password = Password }));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["errors"]!["userName"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe([NameRules.InvalidMessage("User name")]);
        (await FindUser(name)).ShouldBeNull();
    }

    [Test]
    public async Task CreateUser_AUserThatExists_Is409_AtTheUserNameField()
    {
        using var response = await SendAsync(HttpMethod.Post, "/users", content: JsonContent.Create(
            new { userName = "admin", password = Password }));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["errors"]!["userName"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe(["User already exists"]);
    }

    // FR-ADM-07: the e-mail is optional in Edit User
    [Test]
    public async Task UpdateUser_AnEmptyEmail_IsAccepted_AndStoresNoAddress_AnOmittedOneKeepsIt()
    {
        const string user = "noaddress";
        await _factory.CreateUserAsync(user, password: Password);

        using (var omitted = await SendAsync(HttpMethod.Put, $"/users/{user}", content: JsonContent.Create(new { roles = new[] { "viewer" } })))
            omitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FindUser(user)).Email.ShouldBe($"{user}@example.com");
        (await RolesOf(user)).ShouldBe(["viewer"]);

        using (var emptied = await SendAsync(HttpMethod.Put, $"/users/{user}", content: Edit("", "editor")))
            emptied.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FindUser(user)).Email.ShouldBeNull();
        (await RolesOf(user)).ShouldBe(["editor"]);
    }

    [Test]
    public async Task UpdateUser_AnEmailThatIsNotAnAddress_Is400_AtTheEmailField_AndChangesNothing()
    {
        const string user = "badaddress";
        await _factory.CreateUserAsync(user, password: Password);

        using var response = await SendAsync(HttpMethod.Put, $"/users/{user}", content: Edit("not an email", "viewer"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["errors"]!["email"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe([InputRules.InvalidEmailMessage]);
        (await RolesOf(user)).ShouldBeEmpty();
        (await FindUser(user)).Email.ShouldBe($"{user}@example.com");
    }

    [Test]
    public async Task Disable_BlocksSignInAndAccessAtOnce_AndEnableRestoresThem()
    {
        const string user = "disableme";
        var owner = await _factory.CreateUserAsync(user, password: Password);
        using (var scope = _factory.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            Seed.Organization(context, "disableme-org", ownerId: owner.Id, isPublic: false);
            await context.SaveChangesAsync();
        }

        (await SignInAsync(user)).ShouldBe(HttpStatusCode.OK);
        using (var before = await SendAsync(HttpMethod.Get, "/orgs/disableme-org", asUser: user))
            before.StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var disabled = await SendAsync(HttpMethod.Post, $"/users/{user}/disable"))
            disabled.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await SignInAsync(user)).ShouldBe(HttpStatusCode.Unauthorized);
        // The session it has (its identity is still authenticated) loses access with the next request
        using (var after = await SendAsync(HttpMethod.Get, "/orgs/disableme-org", asUser: user))
            after.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using (var enabled = await SendAsync(HttpMethod.Post, $"/users/{user}/enable"))
            enabled.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await SignInAsync(user)).ShouldBe(HttpStatusCode.OK);
        using (var again = await SendAsync(HttpMethod.Get, "/orgs/disableme-org", asUser: user))
            again.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [TestCase("disable")]
    [TestCase("enable")]
    public async Task DisableAndEnable_AnUnknownUser_Are404(string action)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/users/nobody-here/{action}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Disable_YourOwnAccount_Is400()
    {
        using var response = await SendAsync(HttpMethod.Post, $"/users/{Admin}/disable");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RolesOf(Admin)).ShouldNotContain(ApplicationDbContext.DeactivatedRoleName);
    }

    [Test]
    public async Task DisableAndEnable_AreForAdministrators()
    {
        await _factory.CreateUserAsync("plainuser", password: Password);
        await _factory.CreateUserAsync("plaintarget", password: Password);

        using var response = await SendAsync(HttpMethod.Post, "/users/plaintarget/disable", asUser: "plainuser");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RolesOf("plaintarget")).ShouldBeEmpty();
    }

    [Test]
    public async Task UpdateUser_WithoutDeactivated_KeepsTheUserDisabled_AndGrantsAndRemovesAdmin()
    {
        const string user = "edituser";
        await _factory.CreateUserAsync(user, password: Password);
        await AddRole(user, ApplicationDbContext.DeactivatedRoleName);

        using (var granted = await SendAsync(HttpMethod.Put, $"/users/{user}", content: Edit($"{user}@example.com", "editor", "admin")))
            granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOf(user)).ShouldBe(["admin", "deactivated", "editor"]);

        using (var removed = await SendAsync(HttpMethod.Put, $"/users/{user}", content: Edit($"{user}@example.com", "editor")))
            removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOf(user)).ShouldBe(["deactivated", "editor"]);
        (await SignInAsync(user)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task UpdateUser_WithAMistypedRole_Is400_AndChangesNeitherRolesNorEmail()
    {
        const string user = "typouser";
        await _factory.CreateUserAsync(user, password: Password);
        await AddRole(user, "viewer");

        using var response = await SendAsync(HttpMethod.Put, $"/users/{user}", content: Edit("changed@example.com", "edtior"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DetailOf(response)).ShouldContain("edtior");
        (await RolesOf(user)).ShouldBe(["viewer"]);
        (await FindUser(user)).Email.ShouldBe($"{user}@example.com");
    }

    [Test]
    public async Task CreateUser_AnInvalidEmail_Is400_WithTheRule_AndNoEmailIsAllowed()
    {
        using (var refused = await SendAsync(HttpMethod.Post, "/users", content: JsonContent.Create(
                   new { userName = "bademail", password = Password, email = "Jane <jane@example.com>" })))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await DetailOf(refused)).ShouldBe(InputRules.InvalidEmailMessage);
        }

        (await FindUser("bademail")).ShouldBeNull();

        using (var created = await SendAsync(HttpMethod.Post, "/users", content: JsonContent.Create(
                   new { userName = "noemail", password = Password })))
            created.StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var trimmed = await SendAsync(HttpMethod.Post, "/users", content: JsonContent.Create(
                   new { userName = "goodemail", password = Password, email = " good@example.com " })))
            trimmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FindUser("goodemail")).Email.ShouldBe("good@example.com");
    }

    [Test]
    public async Task UpdateUser_AnInvalidNewEmail_Is400_ButAnUnchangedLegacyOneDoesNotBlockTheRoles()
    {
        const string user = "legacyemail";
        await _factory.CreateUserAsync(user, password: Password);
        using (var scope = _factory.Services.CreateScope())
        {
            // An address stored before the rule
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            context.Users.Single(u => u.UserName == user).Email = "legacy-not-an-address";
            await context.SaveChangesAsync();
        }

        using (var refused = await SendAsync(HttpMethod.Put, $"/users/{user}", content: Edit("still not one", "viewer")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await DetailOf(refused)).ShouldBe(InputRules.InvalidEmailMessage);
        }

        (await RolesOf(user)).ShouldBeEmpty();

        using (var kept = await SendAsync(HttpMethod.Put, $"/users/{user}", content: Edit("legacy-not-an-address", "viewer")))
            kept.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOf(user)).ShouldBe(["viewer"]);
    }

    [Test]
    public async Task CreateUserAndUserMeta_WithDeactivated_Are400()
    {
        using (var created = await SendAsync(HttpMethod.Post, "/users", content: JsonContent.Create(
                   new { userName = "createdisabled", password = Password, roles = DeactivatedOnly })))
        {
            created.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await DetailOf(created)).ShouldBe(SystemRoles.DeactivatedInRoleListMessage);
        }

        (await FindUser("createdisabled")).ShouldBeNull();

        await _factory.CreateUserAsync("metadisabled", password: Password);
        using (var meta = await SendAsync(HttpMethod.Post, "/users/metadisabled/meta", content: JsonContent.Create(
                   new { roles = DeactivatedOnly })))
        {
            meta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await DetailOf(meta)).ShouldBe(SystemRoles.DeactivatedInRoleListMessage);
        }

        (await RolesOf("metadisabled")).ShouldBeEmpty();
        (await SignInAsync("metadisabled")).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Startup_SeedsBothSystemRoles_ForAnExternalProvider()
    {
        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var deactivated = await roles.FindByNameAsync(ApplicationDbContext.DeactivatedRoleName);
        (await roles.DeleteAsync(deactivated!)).Succeeded.ShouldBeTrue();
        var external = new Mock<ILoginManager>();
        external.Setup(l => l.Capabilities).Returns(AuthProviderCapabilities.External);

        var provisioner = ActivatorUtilities.CreateInstance<DefaultAdminProvisioner>(scope.ServiceProvider, external.Object);
        await provisioner.EnsureAsync(CancellationToken.None);

        (await roles.RoleExistsAsync(ApplicationDbContext.DeactivatedRoleName)).ShouldBeTrue();
        (await roles.RoleExistsAsync(ApplicationDbContext.AdminRoleName)).ShouldBeTrue();
    }
}
