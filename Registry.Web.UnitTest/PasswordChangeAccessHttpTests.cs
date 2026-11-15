using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Identity.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// PUT /users/{userName}/changepwd: a user other than an administrator changes only their own password, so the current
/// password of another user cannot be tried (nor, when right, replaced) there; a wrong current password of one's own
/// counts toward Identity's lockout as a failed sign-in, and a locked-out user can neither change the password nor sign
/// in until the lockout ends. An administrator still changes any password, with or without the current one.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PasswordChangeAccessHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class PasswordChangeAccessHttpTests : IDisposable
{
    private const string Password = "Password1234";
    private const string NewPassword = "Another-Pass-9";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private int _users;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    // A new user, so that no test sees the failures of another
    private async Task<string> NewUserAsync()
    {
        var name = $"pwlock{++_users}";
        await _factory.CreateUserAsync(name, password: Password);
        return name;
    }

    private async Task<HttpResponseMessage> ChangeAsync(string caller, string userName, string? currentPassword,
        string newPassword = NewPassword, bool isAdmin = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/users/{userName}/changepwd")
        {
            Content = JsonContent.Create(new { currentPassword, newPassword })
        }.AsUser(caller, isAdmin);
        return await _client.SendAsync(request);
    }

    private async Task<HttpStatusCode> SignInAsync(string userName, string password)
    {
        using var response = await _client.PostAsync("/users/authenticate", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["username"] = userName, ["password"] = password }));
        return response.StatusCode;
    }

    private async Task<T> WithUserAsync<T>(string userName, Func<UserManager<User>, User, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return await read(users, (await users.FindByNameAsync(userName))!);
    }

    private static async Task<JsonNode> ProblemOf(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [Test]
    public async Task AUser_CannotChangeThePasswordOfAnother_EvenWithItsCurrentPassword()
    {
        var victim = await NewUserAsync();
        var other = await NewUserAsync();

        using var response = await ChangeAsync(other, victim, Password);

        (await ProblemOf(response, HttpStatusCode.Forbidden))["status"]!.GetValue<int>().ShouldBe(403);
        (await WithUserAsync(victim, (users, user) => users.CheckPasswordAsync(user, Password))).ShouldBeTrue();
        (await WithUserAsync(victim, (users, user) => users.GetAccessFailedCountAsync(user))).ShouldBe(0);
    }

    [Test]
    public async Task AUser_NamingAnotherUserThatDoesNotExist_IsForbidden_NotToldItDoesNotExist()
    {
        var caller = await NewUserAsync();

        using var response = await ChangeAsync(caller, "nosuchuser987", Password);

        await ProblemOf(response, HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task WrongCurrentPasswords_LockTheAccountOut_AsFailedSignInsDo()
    {
        var user = await NewUserAsync();

        for (var attempt = 1; attempt < 5; attempt++)
        {
            using var wrong = await ChangeAsync(user, user, "not-my-password");
            await ProblemOf(wrong, HttpStatusCode.BadRequest);
        }

        // The fifth failure reaches Identity's limit (MaxFailedAccessAttempts)
        using (var fifth = await ChangeAsync(user, user, "not-my-password"))
            await ProblemOf(fifth, HttpStatusCode.TooManyRequests);
        (await WithUserAsync(user, (users, u) => users.IsLockedOutAsync(u))).ShouldBeTrue();

        // Locked out: the right password changes nothing, and the sign-in is refused too
        using (var right = await ChangeAsync(user, user, Password))
        {
            await ProblemOf(right, HttpStatusCode.TooManyRequests);
            right.Headers.RetryAfter.ShouldNotBeNull();
        }

        (await WithUserAsync(user, (users, u) => users.CheckPasswordAsync(u, Password))).ShouldBeTrue();
        (await SignInAsync(user, Password)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task TheRightCurrentPassword_ChangesIt_AndClearsTheFailuresBeforeIt()
    {
        var user = await NewUserAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var wrong = await ChangeAsync(user, user, "not-my-password");
            await ProblemOf(wrong, HttpStatusCode.BadRequest);
        }

        (await WithUserAsync(user, (users, u) => users.GetAccessFailedCountAsync(u))).ShouldBe(3);

        using var response = await ChangeAsync(user, user, Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WithUserAsync(user, (users, u) => users.GetAccessFailedCountAsync(u))).ShouldBe(0);
        (await SignInAsync(user, NewPassword)).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task AnAdministrator_StillChangesAnyPassword_WithOrWithoutTheCurrentOne()
    {
        var user = await NewUserAsync();

        using (var reset = await ChangeAsync(RegistryWebFactory.AdminUserName, user, null, isAdmin: true))
            reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        using (var change = await ChangeAsync(RegistryWebFactory.AdminUserName, user, NewPassword, "Third-Pass-77",
                   isAdmin: true))
            change.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await SignInAsync(user, "Third-Pass-77")).ShouldBe(HttpStatusCode.OK);
    }
}
