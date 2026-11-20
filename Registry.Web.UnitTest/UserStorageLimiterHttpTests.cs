using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// With the storage limiter on, <c>GET /users/storage</c> has the stored quota as its <c>total</c> (in bytes) for a user
/// who is subject to it and none for an administrator, who has no limit whatever is stored; the admin list keeps the
/// stored quota of both. (The limiter off is in UserRolesHttpTests.)
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserStorageLimiterHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class UserStorageLimiterHttpTests : IDisposable
{
    private const string Admin = RegistryWebFactory.AdminUserName;
    private const string Password = "Password1234";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(new Dictionary<string, string> { ["AppSettings:EnableStorageLimiter"] = "true" });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await _factory.CreateUserAsync("limited", password: Password);
        await _factory.CreateUserAsync("boss", isAdmin: true, password: Password);
        foreach (var name in new[] { "limited", "boss" })
        {
            using var set = await SendAsync(HttpMethod.Post, $"/users/{name}/meta", content: JsonContent.Create(new { maxStorageMB = 40 }));
            set.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string asUser = Admin, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.AsUser(asUser, isAdmin: asUser is Admin or "boss");
        return await _client.SendAsync(request);
    }

    private async Task<JsonNode> Json(string url, string asUser = Admin)
    {
        using var response = await SendAsync(HttpMethod.Get, url, asUser);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [Test]
    public async Task Storage_OfAUserSubjectToTheLimiter_HasTheStoredQuotaInBytes()
    {
        (await Json("/users/storage", "limited")).ToJsonString().ShouldBe("""{"total":41943040,"used":0}""");
        (await Json("/users/limited/storage"))["total"]!.GetValue<long>().ShouldBe(40L * 1024 * 1024);
    }

    [Test]
    public async Task Storage_OfAnAdministrator_HasNoTotal_EvenWithAStoredQuota()
    {
        (await Json("/users/storage", "boss")).ToJsonString().ShouldBe("""{"total":null,"used":0}""");
        (await Json("/users/boss/storage"))["total"].ShouldBeNull();
    }

    [Test]
    public async Task UserList_KeepsTheStoredQuotaOfBoth()
    {
        var users = (await Json("/users/detailed")).AsArray();
        foreach (var name in new[] { "limited", "boss" })
            users.ShouldContain(u => u!["userName"]!.GetValue<string>() == name && u["storageQuota"]!.GetValue<long>() == 41943040L);
    }
}
