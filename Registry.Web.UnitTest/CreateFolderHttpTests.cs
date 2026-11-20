using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-DEV-13 through HTTP: <c>POST …/obj</c> with a path and no file part creates a folder and answers its entry
/// as indexed, with its real mtime, not the year 1 (<c>-62135596800</c>).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~CreateFolderHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class CreateFolderHttpTests : IDisposable
{
    private const string Owner = "createfolder-owner";
    private const string Org = "createfolder-org";
    private const string Ds = "createfolder-ds";

    private static readonly DateTime IndexedTime = new(2026, 11, 9, 10, 30, 0, DateTimeKind.Utc);

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private readonly HashSet<string> _added = [];

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // The index: a folder exists once it is added; "unindexed" is added but not found when read back,
        // "unreadable" is added but the index fails when read back (a plain DdbException, which Ddb wraps),
        // "busy" and "broken" fail with exceptions Ddb does not wrap (a DdbException subclass, another exception)
        _factory.DdbWrapper.Setup(d => d.Add(It.IsAny<string>(), It.IsAny<string[]>(), It.IsAny<bool>()))
            .Callback((string _, string[] paths, bool _) =>
            {
                foreach (var path in paths)
                    _added.Add(System.IO.Path.GetFileName(path.TrimEnd('/', '\\')));
            })
            .Returns([]);
        _factory.DdbWrapper.Setup(d => d.GetEntry(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, string path) =>
            {
                if (_added.Contains(path))
                    switch (path)
                    {
                        case "unreadable": throw new DdbException("Cannot read the index");
                        case "busy": throw new DdbBusyException("The index is locked");
                        case "broken": throw new System.IO.IOException("Cannot read the index file");
                    }
                return _added.Contains(path) && path != "unindexed"
                    ? new Entry { Path = path, Type = EntryType.Directory, ModifiedTime = IndexedTime, Depth = 0 }
                    : null;
            });
        // The name lookup of a new folder (D-040) lists nothing: no entry has the name
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.IsAny<EntryQuery>()))
            .Returns([]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task CreateFolder_AnswersTheIndexedEntryWithItsMtime()
    {
        var (status, body) = await CreateFolderAsync("f2");

        status.ShouldBe(HttpStatusCode.Created);
        body["path"]!.GetValue<string>().ShouldBe("f2");
        body["mtime"]!.GetValue<long>().ShouldBe(new DateTimeOffset(IndexedTime).ToUnixTimeSeconds());
    }

    [TestCase("unindexed")]
    [TestCase("unreadable")]
    [TestCase("busy")]
    [TestCase("broken")]
    public async Task CreateFolder_WhenTheIndexCannotGiveItBack_AnswersNow(string path)
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var (status, body) = await CreateFolderAsync(path);

        status.ShouldBe(HttpStatusCode.Created);
        body["path"]!.GetValue<string>().ShouldBe(path);
        body["mtime"]!.GetValue<long>().ShouldBeGreaterThanOrEqualTo(before);
    }

    private async Task<(HttpStatusCode Status, JsonObject Body)> CreateFolderAsync(string path)
    {
        using var content = new MultipartFormDataContent { { new StringContent(path), "path" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/obj") { Content = content }
            .AsUser(Owner);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonNode.Parse(text)!.AsObject());
    }
}
