using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Services.Adapters;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/>: the streamed downloads (the dataset index, ZIP
/// archives) are written by one <c>StreamedDownloadResult</c> instead of a hand-built header in the controller.
/// The body and the headers are the ones it wrote (<c>application/zip</c>, <c>Cache-Control: no-store</c>, the
/// archive bytes) except <c>Content-Disposition</c>, which carries the RFC 6266 <c>filename*</c> next to the
/// quoted <c>filename</c> it had.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DownloadHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class DownloadHttpTests : IDisposable
{
    private const string OwnerUser = "downloadhttp-download-owner";
    private const string Org = "downloadhttp-dl-org";
    private const string Ds = "downloadhttp-dl-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private IDDB _ddb = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // The index is checkpointed before it is archived
        _factory.DdbWrapper.Setup(d => d.Checkpoint(It.IsAny<string>())).Returns(true);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();

        _ddb = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef);
        var index = Path.Combine(_ddb.DatasetFolderPath, IDDB.DatabaseFolderName);
        Directory.CreateDirectory(index);
        await File.WriteAllTextAsync(Path.Combine(index, IDDB.DatabaseFileName), "the index");
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task DatasetIndex_IsTheSameArchive_WithTheSameHeaders_AndTheRfc6266FileName()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/ddb").AsUser(OwnerUser);
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        using var expected = new MemoryStream();
        await new ZipArchiveBuilder().WriteZipAsync(_ddb, [], [], includeDdb: true, expected);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("application/zip");
        response.Headers.CacheControl!.ToString().ShouldBe("no-store");
        body.ShouldBe(expected.ToArray());
        response.Content.Headers.GetValues("Content-Disposition").Single()
            .ShouldBe($"attachment; filename=\"{Org}-{Ds}-ddb.zip\"; filename*=UTF-8''{Org}-{Ds}-ddb.zip");
    }
}

/// <summary>
/// With <c>DisableAnonymousBulkDownloads</c> on, the dataset index (<c>GET .../ddb</c>), an archive like the others,
/// is refused to an anonymous caller of a public dataset with 401, and a signed-in user gets it.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~AnonymousBulkDownloadHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class AnonymousBulkDownloadHttpTests : IDisposable
{
    private const string OwnerUser = "bulkdownload-owner";
    private const string Org = "bulkdownload-org";
    private const string Ds = "bulkdownload-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(new Dictionary<string, string>
        {
            ["AppSettings:DisableAnonymousBulkDownloads"] = "true"
        });
        // A public dataset: anyone may read it (the visibility of the index meta)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "visibility", It.IsAny<string?>()))
            .Returns($$"""{"data":{{(int)Visibility.Public}},"id":"visibility","mtime":0}""");
        _factory.DdbWrapper.Setup(d => d.Checkpoint(It.IsAny<string>())).Returns(true);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: true);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();

        var ddb = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef);
        var index = Path.Combine(ddb.DatasetFolderPath, IDDB.DatabaseFolderName);
        Directory.CreateDirectory(index);
        await File.WriteAllTextAsync(Path.Combine(index, IDDB.DatabaseFileName), "the index");
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task DatasetIndex_IsRefusedToAnAnonymousCaller_AndGivenToASignedInUser()
    {
        var url = $"/orgs/{Org}/ds/{Ds}/ddb";

        using var anonymous = await _client.GetAsync(url);
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var signedIn = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).AsUser(OwnerUser));
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        signedIn.Content.Headers.ContentType!.ToString().ShouldBe("application/zip");
    }
}
