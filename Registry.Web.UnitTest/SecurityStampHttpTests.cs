using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NUnit.Framework;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Test.Support;
using Registry.Web.Utilities.Auth;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// A password change ends the other sessions of the user: the JWT carries the security stamp the user had when it was
/// issued, and a token with an old stamp is refused on every request and on a refresh, at once (the snapshot cache is
/// invalidated), while the token that the change itself returns carries the new stamp. A token without the claim,
/// issued before it existed, is accepted until it expires.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~SecurityStampHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class SecurityStampHttpTests
{
    private const string Password = "Password1234";
    private const string NewPassword = "Another5678pw";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new RegistryWebFactory();
        await _factory.CreateUserAsync("sessions", password: Password);
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<string> SignInAsync()
    {
        using var response = await _client.PostAsync("/users/authenticate", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["username"] = "sessions", ["password"] = Password }));
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await TokenOfAsync(response);
    }

    private static async Task<string> TokenOfAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    private async Task<HttpStatusCode> SendAsync(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        return response.StatusCode;
    }

    private Task<HttpStatusCode> MeAsync(string token) => SendAsync(HttpMethod.Get, "/users/me", token);

    private Task<HttpStatusCode> RefreshAsync(string token) =>
        SendAsync(HttpMethod.Post, "/users/authenticate/refresh", token);

    private async Task<string> ChangePasswordAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/changepwd")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["oldPassword"] = Password, ["newPassword"] = NewPassword
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await TokenOfAsync(response);
    }

    [Test]
    public async Task AfterAPasswordChange_TheOtherTokens_AreRefusedAtOnce_AndTheReturnedOneWorks()
    {
        var other = await SignInAsync();
        var changing = await SignInAsync();
        // The other session was seen just before the change: its stamp is in the snapshot cache
        (await MeAsync(other)).ShouldBe(HttpStatusCode.OK);

        var returned = await ChangePasswordAsync(changing);

        (await MeAsync(other)).ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(other)).ShouldBe(HttpStatusCode.Unauthorized);
        (await MeAsync(changing)).ShouldBe(HttpStatusCode.Unauthorized);

        // The Hub stores the token the change returns: the user who changed the password stays signed in
        (await MeAsync(returned)).ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(returned)).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task ARefresh_IssuesATokenWithTheCurrentStamp()
    {
        var token = await SignInAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/authenticate/refresh");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var refreshed = await TokenOfAsync(response);

        new JwtSecurityTokenHandler().ReadJwtToken(refreshed).Claims
            .ShouldContain(c => c.Type == Policies.SecurityStampClaimType && c.Value.Length > 0);
        (await MeAsync(refreshed)).ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task ATokenWithoutAStamp_IsAcceptedUntilItExpires()
    {
        var user = await UserIdAsync();
        var unstamped = IssueWithoutStamp(user);

        (await MeAsync(unstamped)).ShouldBe(HttpStatusCode.OK);

        // A password change does not end it (it has no stamp to compare), the refresh gives the stamped token
        await ChangePasswordAsync(await SignInAsync());
        (await MeAsync(unstamped)).ShouldBe(HttpStatusCode.OK);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/authenticate/refresh");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", unstamped);
        using var response = await _client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        new JwtSecurityTokenHandler().ReadJwtToken(await TokenOfAsync(response)).Claims
            .ShouldContain(c => c.Type == Policies.SecurityStampClaimType);
    }

    [Test]
    public async Task AnAdminReset_EndsTheSessionsOfTheUser()
    {
        var session = await SignInAsync();
        (await MeAsync(session)).ShouldBe(HttpStatusCode.OK);

        using var request = new HttpRequestMessage(HttpMethod.Put, "/users/sessions/changepwd")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { newPassword = NewPassword }),
                Encoding.UTF8, "application/json")
        }.AsUser(RegistryWebFactory.AdminUserName, isAdmin: true);
        using var reset = await _client.SendAsync(request);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await MeAsync(session)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<string> UserIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Registry.Web.Identity.Models.User>>();
        return (await users.FindByNameAsync("sessions"))!.Id;
    }

    // A token as the issuer made them before the stamp: the user id and the admin flag only
    private string IssueWithoutStamp(string userId)
    {
        var secret = _factory.Services.GetRequiredService<IOptions<AuthTokenOptions>>().Value.Secret!;
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim(ClaimTypes.Name, userId),
                new Claim(ApplicationDbContext.AdminRoleName.ToLowerInvariant(), "False", ClaimValueTypes.Boolean)
            ]),
            Expires = DateTime.UtcNow.AddDays(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.ASCII.GetBytes(secret)),
                SecurityAlgorithms.HmacSha256Signature)
        }));
    }
}
