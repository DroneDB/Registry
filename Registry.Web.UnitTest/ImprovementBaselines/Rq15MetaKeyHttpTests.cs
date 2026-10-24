#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// Found by RQ-14's A/B run: <c>POST …/meta/set/tags</c> and <c>GET …/meta/get/tags</c> answered 500. libddb's key
/// rule (a key ending with "s" is a list: <c>add</c>, not <c>set</c>) is reported by the <c>DDBMeta*</c> exports as
/// <c>DDBERR_EXCEPTION</c> with a message (ABI 2.0 keeps the old codes), and a key with no value is the same kind of
/// failure: Registry could only answer 500 for what the client got wrong. The key rule is checked first
/// (<see cref="Registry.Web.Utilities.MetaKeyRule"/>, 400 with libddb's message, libddb is never called) and a read
/// that fails for a key nobody wrote is a 404, decided by asking libddb which keys exist, not by reading its
/// message. Any other failure of the read stays what it was.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq15MetaKeyHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class Rq15MetaKeyHttpTests : IDisposable
{
    private const string OwnerUser = "rq15-meta-owner";
    private const string Org = "rq15-meta-org";
    private const string Ds = "rq15-meta-ds";
    private const string Api = $"/orgs/{Org}/ds/{Ds}/meta";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
    }

    [SetUp]
    public void SetUp()
    {
        _factory.DdbWrapper.Reset();
        _factory.DdbWrapper.CallBase = true;
        _factory.DdbWrapper.Setup(d => d.Init(It.IsAny<string>()))
            .Returns((string directory) => System.IO.Path.Combine(directory, IDDB.DatabaseFolderName));
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "visibility", It.IsAny<string?>()))
            .Throws(new DdbException("No metadata found for key visibility"));
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private async Task<(int Status, JsonObject? Body)> SendAsync(HttpMethod method, string url, string? json = null,
        FormUrlEncodedContent? form = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (form != null)
            request.Content = form;
        request.AsUser(OwnerUser);

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject);
    }

    private void ShouldNeverCallLibddbToWrite() =>
        _factory.DdbWrapper.Verify(d => d.MetaSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>()), Times.Never);

    [Test]
    public async Task SetOnAListKey_Is400WithTheRuleOfLibddb()
    {
        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Api}/set/tags", "\"survey\"");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem!["detail"]!.GetValue<string>().ShouldBe("Invalid metadata key (must be singular, for example: tag)");
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
        ShouldNeverCallLibddbToWrite();
    }

    [Test]
    public async Task SetOnAListKey_ByForm_Is400()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["key"] = "tags", ["data"] = "survey" });

        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Api}/set", form: form);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem!["detail"]!.GetValue<string>().ShouldBe("Invalid metadata key (must be singular, for example: tag)");
        ShouldNeverCallLibddbToWrite();
    }

    [Test]
    public async Task AddOnASingleKey_Is400WithTheRuleOfLibddb()
    {
        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Api}/add/name", "\"survey\"");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem!["detail"]!.GetValue<string>().ShouldBe("Invalid metadata key (must be plural, for example: names)");
        _factory.DdbWrapper.Verify(d => d.MetaAdd(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Test]
    public async Task AKeyWithUppercase_Is400()
    {
        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Api}/set/Name", "\"survey\"");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem!["detail"]!.GetValue<string>().ShouldBe("Metadata key must be lowercase");
        ShouldNeverCallLibddbToWrite();
    }

    [Test]
    public async Task AddOnAListKey_ReachesLibddb()
    {
        _factory.DdbWrapper.Setup(d => d.MetaAdd(It.IsAny<string>(), "tags", "survey", It.IsAny<string?>()))
            .Returns(new Meta { Id = "1", ModifiedTime = DateTime.UnixEpoch });

        var (status, body) = await SendAsync(HttpMethod.Post, $"{Api}/add/tags", "\"survey\"");

        status.ShouldBe(StatusCodes.Status200OK);
        body!["id"]!.GetValue<string>().ShouldBe("1");
    }

    [Test]
    public async Task GetOfAKeyNobodyWrote_Is404()
    {
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "tags", It.IsAny<string?>()))
            .Throws(new DdbException("No metadata found for key tags"));
        _factory.DdbWrapper.Setup(d => d.MetaList(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new List<MetaListItem> { new() { Key = "name", Path = "", Count = 1 } });

        var (status, problem) = await SendAsync(HttpMethod.Get, $"{Api}/get/tags");

        status.ShouldBe(StatusCodes.Status404NotFound);
        problem!["detail"]!.GetValue<string>().ShouldBe("No metadata found for key tags");
    }

    [Test]
    public async Task GetThatFailsForAKeyThatExists_StaysAServerError()
    {
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "tags", It.IsAny<string?>()))
            .Throws(new DdbException("disk I/O error"));
        _factory.DdbWrapper.Setup(d => d.MetaList(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new List<MetaListItem> { new() { Key = "tags", Path = "", Count = 2 } });

        var (status, _) = await SendAsync(HttpMethod.Get, $"{Api}/get/tags");

        status.ShouldBe(StatusCodes.Status500InternalServerError);
    }

    [Test]
    public async Task GetThatIsBusy_IsNotTurnedIntoANotFound()
    {
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "tags", It.IsAny<string?>()))
            .Throws(new DdbBusyException("database is locked"));

        var (status, _) = await SendAsync(HttpMethod.Get, $"{Api}/get/tags");

        status.ShouldNotBe(StatusCodes.Status404NotFound);
        status.ShouldBeGreaterThanOrEqualTo(500);
    }
}
