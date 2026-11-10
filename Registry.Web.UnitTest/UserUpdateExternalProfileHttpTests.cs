using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-ADM-07: when the identity provider manages the profile, Edit User can still edit the roles if the request leaves the
/// e-mail out (or sends the address the user has): only a change of the address is refused (403). When the provider manages
/// the roles as well, the roles are refused with their own message and nothing changes.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserUpdateExternalProfileHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class UserUpdateExternalProfileHttpTests : IDisposable
{
    private const string Admin = RegistryWebFactory.AdminUserName;

    // What the provider of the host manages: a provider that takes the profile from the directory but not the roles
    private sealed class Provider : ILoginManager
    {
        public static AuthProviderCapabilities Current { get; set; } = AuthProviderCapabilities.Local;

        public Task<LoginResultDto> CheckAccess(string userName, string password) => throw new NotSupportedException();
        public Task<LoginResultDto> CheckAccess(string token) => throw new NotSupportedException();
        public AuthProviderCapabilities Capabilities => Current;
    }

    private static readonly AuthProviderCapabilities ProfileOnly = AuthProviderCapabilities.External with { ManagesRolesExternally = false };

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(configureServices: services =>
        {
            services.RemoveAll<ILoginManager>();
            services.AddScoped<ILoginManager, Provider>();
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var scope = _factory.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "editor", "viewer" })
            if (!await roles.RoleExistsAsync(role))
                (await roles.CreateAsync(new IdentityRole(role))).Succeeded.ShouldBeTrue();
    }

    [TearDown]
    public void TearDown() => Provider.Current = AuthProviderCapabilities.Local;

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<HttpResponseMessage> PutAsync(string user, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/users/{user}") { Content = JsonContent.Create(body) };
        request.AsUser(Admin, isAdmin: true);
        return await _client.SendAsync(request);
    }

    // The address and the roles of the user as "address|role,role"
    private async Task<string> StateOf(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByNameAsync(userName))!;
        return $"{user.Email}|{string.Join(',', (await users.GetRolesAsync(user)).OrderBy(r => r))}";
    }

    private async Task<string> DetailOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["detail"]!.GetValue<string>();

    [Test]
    public async Task ProfileManagedByTheProvider_ARolesOnlyUpdate_WithoutTheEmail_IsAccepted_AndKeepsTheAddress()
    {
        await _factory.CreateUserAsync("ldapuser", password: "Password1234");
        Provider.Current = ProfileOnly;

        using var response = await PutAsync("ldapuser", new { roles = new[] { "viewer" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StateOf("ldapuser")).ShouldBe("ldapuser@example.com|viewer");
    }

    [Test]
    public async Task ProfileManagedByTheProvider_TheAddressTheUserHas_IsAccepted_ADifferentOne_Is403_AndChangesNothing()
    {
        await _factory.CreateUserAsync("ldapsame", password: "Password1234");
        Provider.Current = ProfileOnly;

        using (var same = await PutAsync("ldapsame", new { email = " ldapsame@example.com ", roles = new[] { "editor" } }))
            same.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StateOf("ldapsame")).ShouldBe("ldapsame@example.com|editor");

        using (var changed = await PutAsync("ldapsame", new { email = "other@example.com", roles = new[] { "viewer" } }))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await DetailOf(changed)).ShouldBe("Profile data (email) is synchronised from the identity provider and cannot be modified locally.");
        }

        using (var emptied = await PutAsync("ldapsame", new { email = "", roles = new[] { "viewer" } }))
            emptied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await StateOf("ldapsame")).ShouldBe("ldapsame@example.com|editor");
    }

    [Test]
    public async Task ProfileAndRolesManagedByTheProvider_TheRoles_AreRefused_WithTheirOwnMessage_AndNothingChanges()
    {
        await _factory.CreateUserAsync("ldapboth", password: "Password1234");
        Provider.Current = AuthProviderCapabilities.External;

        using var response = await PutAsync("ldapboth", new { roles = new[] { "viewer" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DetailOf(response)).ShouldContain("Role assignments are managed by the identity provider");
        (await StateOf("ldapboth")).ShouldBe("ldapboth@example.com|");
    }

    [Test]
    public async Task LocalProvider_ARolesOnlyUpdate_WithoutTheEmail_StillKeepsTheAddress()
    {
        await _factory.CreateUserAsync("localuser", password: "Password1234");

        using var response = await PutAsync("localuser", new { roles = new[] { "editor" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StateOf("localuser")).ShouldBe("localuser@example.com|editor");
    }
}
