using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// FR-DEV-13 on the real libddb: <c>POST …/obj</c> with <c>path=f2</c> and no file part answers 201 with the entry of
/// the new folder as indexed, its mtime (Unix seconds, above 0) equal to the one of <c>GET …/list</c>.
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~CreateFolderNativeTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Native)]
public sealed class CreateFolderNativeTests : IDisposable
{
    private const string OwnerUser = "createfolder-native-owner";
    private const string Org = "createfolder-native-org";
    private const string Ds = "createfolder-native-ds";
    private const string DsApi = $"/orgs/{Org}/ds/{Ds}";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(configureServices: services =>
        {
            services.RemoveAll<IDdbWrapper>();
            services.AddSingleton<IDdbWrapper>(new NativeDdbWrapper());
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
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

    [TestCase("f2")]
    [TestCase("emptyfolder")]
    public async Task CreateFolder_AnswersTheEntryOfTheListWithItsMtime(string path)
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5;

        using var content = new MultipartFormDataContent { { new StringContent(path), "path" } };
        using var post = new HttpRequestMessage(HttpMethod.Post, $"{DsApi}/obj") { Content = content }.AsUser(OwnerUser);
        using var created = await _client.SendAsync(post);
        var createdBody = await created.Content.ReadAsStringAsync();
        created.StatusCode.ShouldBe(HttpStatusCode.Created, createdBody);
        var folder = JsonNode.Parse(createdBody)!;

        using var get = new HttpRequestMessage(HttpMethod.Get, $"{DsApi}/list").AsUser(OwnerUser);
        using var listed = await _client.SendAsync(get);
        var listBody = await listed.Content.ReadAsStringAsync();
        listed.StatusCode.ShouldBe(HttpStatusCode.OK, listBody);
        var entry = JsonNode.Parse(listBody)!.AsArray().Single(e => e!["path"]!.GetValue<string>() == path)!;

        var mtime = folder["mtime"]!.GetValue<long>();
        mtime.ShouldBeGreaterThanOrEqualTo(before);
        mtime.ShouldBe(entry["mtime"]!.GetValue<long>());
        folder["path"]!.GetValue<string>().ShouldBe(path);
    }
}
