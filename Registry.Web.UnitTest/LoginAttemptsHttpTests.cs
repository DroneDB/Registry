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
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Users;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// POST /users/authenticate: a wrong password counts toward Identity's lockout (5 failures lock the account for 5
/// minutes, and a locked account is refused with 429 and Retry-After even with its right password), and each client
/// address has a few attempts per minute (SignInThrottle), whatever accounts it tries. The same attempts are counted for
/// HTTP Basic credentials (OGC clients), but only the wrong ones: a client with the right password sends them with every
/// request and is never limited. Each test has its own application, so it starts with a fresh window of attempts.
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
        for (var i = 0; i < SignInThrottle.PermitsPerMinute; i++)
        {
            using var attempt = await SignInAsync($"nobody{i}", "a-password");
            attempt.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var over = await SignInAsync("nobody-else", "a-password");
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        over.Headers.RetryAfter.ShouldNotBeNull();
    }
    [Test]
    public async Task TheSignInLimit_AnswersProblemDetails_WithRetryAfter()
    {
        for (var i = 0; i < SignInThrottle.PermitsPerMinute; i++)
        {
            using var attempt = await SignInAsync("nobody", "a-password");
            attempt.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var over = await SignInAsync("nobody", "a-password");
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        over.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        over.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        using var body = JsonDocument.Parse(await over.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("status").GetInt32().ShouldBe(429);
        body.RootElement.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task WrongBasicCredentials_AreLimitedLikeTheSignIn_AndTheLimitedOnesNeverReachTheLoginManager()
    {
        var logins = new CountingLoginManager();
        using var factory = CountingFactory(logins);
        using var client = factory.CreateClient();

        for (var i = 0; i < SignInThrottle.PermitsPerMinute; i++)
        {
            using var wrong = await client.SendAsync(WmsRequest("qgis", "not-the-password"));
            wrong.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }
        logins.Calls.ShouldBe(SignInThrottle.PermitsPerMinute);

        using var over = await client.SendAsync(WmsRequest("qgis", "not-the-password"));
        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        over.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        over.Headers.RetryAfter.ShouldNotBeNull();
        logins.Calls.ShouldBe(SignInThrottle.PermitsPerMinute);

        // The address is spent for every way of presenting a password, the sign-in included
        using var signIn = await client.PostAsync("/users/authenticate", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["username"] = "qgis", ["password"] = Password }));
        signIn.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task RightBasicCredentials_AreNeverLimited()
    {
        var logins = new CountingLoginManager();
        using var factory = CountingFactory(logins);
        await factory.CreateUserAsync("qgis", password: Password);
        using var client = factory.CreateClient();

        // A map client sends its credentials with every tile: far more than the sign-in allows
        for (var i = 0; i < 5 * SignInThrottle.PermitsPerMinute; i++)
        {
            using var response = await client.SendAsync(WmsRequest("qgis", Password));
            response.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }
        logins.Calls.ShouldBe(5 * SignInThrottle.PermitsPerMinute);

        // ... and they did not use up the attempts of wrong ones
        using var wrong = await client.SendAsync(WmsRequest("qgis", "not-the-password"));
        wrong.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }

    private static RegistryWebFactory CountingFactory(CountingLoginManager logins) =>
        new(configureServices: services =>
        {
            services.RemoveAll<ILoginManager>();
            services.AddScoped<ILoginManager>(sp =>
                logins.Around(ActivatorUtilities.CreateInstance<LocalLoginManager>(sp)));
        });

    private static HttpRequestMessage WmsRequest(string userName, string password) =>
        new(HttpMethod.Get, "/orgs/qgis-org/ds/qgis-ds/wms?service=WMS&request=GetCapabilities")
        {
            Headers =
            {
                Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}")))
            }
        };

    /// <summary>Counts the passwords the login provider is asked to check; the checks are the real ones.</summary>
    private sealed class CountingLoginManager
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ILoginManager Around(ILoginManager inner) => new Counted(this, inner);

        private sealed class Counted(CountingLoginManager owner, ILoginManager inner) : ILoginManager
        {
            public Task<LoginResultDto> CheckAccess(string userName, string password)
            {
                Interlocked.Increment(ref owner._calls);
                return inner.CheckAccess(userName, password);
            }

            public Task<LoginResultDto> CheckAccess(string token) => inner.CheckAccess(token);

            public AuthProviderCapabilities Capabilities => inner.Capabilities;
        }
    }
}
