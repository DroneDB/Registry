using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using NUnit.Framework;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// D-047, FR-XC-11, FR-ADM-08, FR-ACC-21 and the API row "Refusals of one input field" (§7.1) through HTTP: the two
/// password-change endpoints refuse a wrong current password and a password that breaks the policy with a 400
/// ValidationProblemDetails (<c>application/problem+json</c>) whose <c>errors</c> member names the request member
/// (<c>currentPassword</c>, <c>newPassword</c>) and carries the text without Identity's "Code - " prefixes; the status
/// and <c>detail</c> stay for clients that do not read <c>errors</c>; a refusal that concerns no field has no
/// <c>errors</c> member.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PasswordFieldErrorsHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class PasswordFieldErrorsHttpTests : IDisposable
{
    private const string Admin = RegistryWebFactory.AdminUserName;
    private const string User = "pwuser";
    private const string Password = "Password1234";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(new Dictionary<string, string>
        {
            ["AppSettings:PasswordPolicy:MinLength"] = "8",
            ["AppSettings:PasswordPolicy:RequireDigit"] = "true",
            ["AppSettings:PasswordPolicy:RequireUppercase"] = "false",
            ["AppSettings:PasswordPolicy:RequireLowercase"] = "false",
            ["AppSettings:PasswordPolicy:RequireNonAlphanumeric"] = "false"
        });
        _client = _factory.CreateClient();
        await _factory.CreateUserAsync(User, password: Password);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<HttpResponseMessage> SelfServiceAsync(string oldPassword, string newPassword)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/users/changepwd")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["oldPassword"] = oldPassword,
                ["newPassword"] = newPassword
            })
        }.AsUser(User);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> AdminAsync(string userName, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/users/{userName}/changepwd")
        {
            Content = JsonContent.Create(body)
        }.AsUser(Admin, isAdmin: true);
        return await _client.SendAsync(request);
    }

    private static async Task<JsonNode> ProblemOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static string[] Errors(JsonNode problem, string field) =>
        [.. problem["errors"]![field]!.AsArray().Select(e => e!.GetValue<string>())];

    [Test]
    public async Task SelfService_AWrongCurrentPassword_IsAnErrorOfCurrentPassword()
    {
        using var response = await SelfServiceAsync("not-my-password", "Another-Pass-9");

        var problem = await ProblemOf(response);
        Errors(problem, "currentPassword").ShouldBe(["The current password is incorrect."]);
        problem["errors"]!.AsObject().Count.ShouldBe(1);
        problem["status"]!.GetValue<int>().ShouldBe(400);
        problem["detail"]!.GetValue<string>().ShouldStartWith("Cannot change user password: ");
    }

    [Test]
    public async Task SelfService_ATooShortNewPassword_IsAnErrorOfNewPassword_WithoutCodes()
    {
        using var response = await SelfServiceAsync(Password, "abc");

        var problem = await ProblemOf(response);
        Errors(problem, "newPassword").ShouldBe(["Password must be at least 8 characters long Password must contain at least one digit"]);
        problem["errors"]!.AsObject().Count.ShouldBe(1);
        problem["detail"]!.GetValue<string>().ShouldStartWith("Password does not meet the requirements");
    }

    [Test]
    public async Task Admin_AWrongCurrentPassword_IsAnErrorOfCurrentPassword()
    {
        using var response = await AdminAsync(User, new { currentPassword = "not-my-password", newPassword = "Another-Pass-9" });

        var problem = await ProblemOf(response);
        Errors(problem, "currentPassword").ShouldBe(["The current password is incorrect."]);
    }

    [Test]
    public async Task Admin_ResetToATooShortPassword_IsAnErrorOfNewPassword_WithoutCodes()
    {
        using var response = await AdminAsync(User, new { newPassword = "abc" });

        var problem = await ProblemOf(response);
        Errors(problem, "newPassword").ShouldBe(["Passwords must be at least 8 characters. Passwords must have at least one digit ('0'-'9')."]);
        problem["errors"]!.AsObject().Count.ShouldBe(1);
        problem["detail"]!.GetValue<string>().ShouldContain("PasswordTooShort");
    }

    [Test]
    public async Task Admin_AnUnknownUser_HasNoErrorsMember()
    {
        using var response = await AdminAsync("nosuchuser123", new { currentPassword = Password, newPassword = "Another-Pass-9" });

        var problem = await ProblemOf(response);
        problem["detail"]!.GetValue<string>().ShouldBe("User does not exist");
        problem.AsObject().ContainsKey("errors").ShouldBeFalse();
    }
}
