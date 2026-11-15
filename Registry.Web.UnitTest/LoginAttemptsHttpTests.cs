using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Identity.Models;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// POST /users/authenticate: a wrong password counts toward Identity's lockout (5 failures lock the account for 5
/// minutes, and a locked account is refused with 429 and Retry-After even with its right password), and each client
/// address has a few attempts per minute (RateLimitPolicies.Login), whatever accounts it tries. Each test has its own
/// application, so it starts with a fresh window of attempts.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~LoginAttemptsHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class LoginAttemptsHttpTests
{
    private const string Password = "Password1234";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [SetUp]
    public void SetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<HttpResponseMessage> SignInAsync(string userName, string password) =>
        await _client.PostAsync("/users/authenticate", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["username"] = userName, ["password"] = password }));

    private async Task<int> FailuresOfAsync(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return await users.GetAccessFailedCountAsync((await users.FindByNameAsync(userName))!);
    }

    [Test]
    public async Task FiveWrongPasswords_LockTheAccount_AndItsRightPasswordIsRefused()
    {
        await _factory.CreateUserAsync("guessed", password: Password);

        for (var i = 1; i < 5; i++)
        {
            using var wrong = await SignInAsync("guessed", "not-the-password");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        (await FailuresOfAsync("guessed")).ShouldBe(4);

        // The fifth failure reaches Identity's limit (MaxFailedAccessAttempts)
        using (var fifth = await SignInAsync("guessed", "not-the-password"))
            fifth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        using var right = await SignInAsync("guessed", Password);
        right.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        right.Headers.RetryAfter.ShouldNotBeNull();
        right.Headers.RetryAfter.Delta.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Test]
    public async Task ARightPassword_SignsIn_AndClearsTheFailures()
    {
        await _factory.CreateUserAsync("forgetful", password: Password);

        for (var i = 0; i < 3; i++)
        {
            using var wrong = await SignInAsync("forgetful", "not-the-password");
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
        (await FailuresOfAsync("forgetful")).ShouldBe(3);

        using var right = await SignInAsync("forgetful", Password);
        right.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await FailuresOfAsync("forgetful")).ShouldBe(0);
    }

    [Test]
    public async Task OneClientAddress_HasAFewAttemptsPerMinute_WhateverAccountsItTries()
    {
        // Accounts that do not exist: no lockout can answer, only the limit of the address
        for (var i = 0; i < RateLimitPolicies.LoginPermitsPerMinute; i++)
        {
            using var attempt = await SignInAsync($"nobody{i}", "a-password");
            attempt.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var over = await SignInAsync("nobody-else", "a-password");
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        over.Headers.RetryAfter.ShouldNotBeNull();
    }
}
