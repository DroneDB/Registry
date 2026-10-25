#nullable enable
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Through HTTP: the task result and the MVT tile are served by <c>PhysicalFile</c> with their validators
/// (last modified and entity tag) instead of an <c>ETag</c> header set by hand, so the framework answers the
/// conditional and range requests: <c>If-None-Match</c> → 304, <c>Range</c> → 206, <c>If-Range</c> with another
/// tag → the whole file. The tag values clients may have cached are unchanged (the artifact checksum, and
/// <c>"{hash}-{z}-{x}-{y}"</c> for a tile). Before, both actions set <c>ETag</c> and never answered 304, and the task
/// result ignored <c>If-Range</c>.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~FileValidatorHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class FileValidatorHttpTests : IDisposable
{
    private const string OwnerUser = "filevalidatorhttp-files-owner";
    private const string Org = "filevalidatorhttp-files-org";
    private const string Ds = "filevalidatorhttp-files-ds";
    private const string TaskId = "task-1";
    private const string ResultTag = "\"0123456789abcdef\"";
    private const string MvtHash = "mvthash";
    private static readonly byte[] Content = "0123456789ABCDEFGHIJ"u8.ToArray();

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _files = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _files = Path.Combine(Path.GetTempPath(), "filevalidatorhttp-files", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_files);
        var result = Path.Combine(_files, "result.zip");
        var tile = Path.Combine(_files, "7.pbf");
        await File.WriteAllBytesAsync(result, Content);
        await File.WriteAllBytesAsync(tile, Content);

        var tasks = new Mock<ITasksManager>();
        tasks.Setup(t => t.GetResultAsync(Org, Ds, TaskId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TaskArtifactFile(result, "result.zip", "application/zip", ResultTag));
        var artifacts = new Mock<IBuildArtifactResolver>();
        artifacts.Setup(a => a.FindArtifactFile(It.IsAny<IDDB>(), MvtHash, ArtifactRoles.VectorMvt, "3/5/7.pbf", null))
            .Returns(tile);

        _factory = new RegistryWebFactory(configureServices: services =>
        {
            services.RemoveAll<ITasksManager>();
            services.AddScoped(_ => tasks.Object);
            services.RemoveAll<IBuildArtifactResolver>();
            services.AddSingleton(artifacts.Object);
        });
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
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
        if (_files != null && Directory.Exists(_files))
            Directory.Delete(_files, true);
    }

    private const string ResultUrl = $"/orgs/{Org}/ds/{Ds}/tasks/{TaskId}/result";
    private const string TileUrl = $"/orgs/{Org}/ds/{Ds}/mvt/{MvtHash}/3/5/7.pbf";
    private const string TileTag = $"\"{MvtHash}-3-5-7\"";

    private async Task<HttpResponseMessage> GetAsync(string url, Action<HttpRequestMessage>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url).AsUser(OwnerUser);
        headers?.Invoke(request);
        return await _client.SendAsync(request);
    }

    [TestCase(ResultUrl, ResultTag)]
    [TestCase(TileUrl, TileTag)]
    public async Task TheFile_HasItsTag_AndLastModified(string url, string tag)
    {
        using var response = await GetAsync(url);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.ToString().ShouldBe(tag);
        response.Content.Headers.LastModified.ShouldNotBeNull();
        response.Headers.AcceptRanges.ShouldContain("bytes");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Content);
    }

    [TestCase(ResultUrl, ResultTag)]
    [TestCase(TileUrl, TileTag)]
    public async Task IfNoneMatch_WithItsTag_Is304(string url, string tag)
    {
        using var response = await GetAsync(url, r => r.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(tag)));

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Headers.ETag!.ToString().ShouldBe(tag);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    [TestCase(ResultUrl)]
    [TestCase(TileUrl)]
    public async Task Range_Is206_WithThatPart(string url)
    {
        using var response = await GetAsync(url, r => r.Headers.Range = new RangeHeaderValue(5, 9));

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().ShouldBe($"bytes 5-9/{Content.Length}");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Content[5..10]);
    }

    [Test]
    public async Task IfRange_WithAnotherTag_IsTheWholeFile()
    {
        using var response = await GetAsync(ResultUrl, r =>
        {
            r.Headers.Range = new RangeHeaderValue(5, 9);
            r.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse("\"another\""));
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Content);
    }

    [Test]
    public async Task TheTile_KeepsItsCachingHeaders()
    {
        using var response = await GetAsync(TileUrl);

        response.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.mapbox-vector-tile");
        response.Headers.CacheControl!.ToString().ShouldBe("public, max-age=31536000, immutable");
    }

    [Test]
    public async Task TheResult_KeepsItsDownloadName()
    {
        using var response = await GetAsync(ResultUrl);

        response.Content.Headers.ContentDisposition!.FileName.ShouldBe("result.zip");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
    }
}
