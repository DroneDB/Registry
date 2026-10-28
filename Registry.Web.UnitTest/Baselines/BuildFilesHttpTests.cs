#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/>: the build endpoints read
/// libddb's build manifest instead of composing the build layout.
/// <list type="bullet">
/// <item><c>GET build/{hash}/{path}</c> of a file that is not part of the build was a 500 (the composed path
/// went to <c>PhysicalFile</c>, which threw <c>FileNotFoundException</c> while the result executed); libddb
/// resolves the file now (<c>DDBResolveBuildFile</c>), so it is a 404.</item>
/// <item><c>GET</c> and <c>HEAD</c> of a build file send the manifest's media type when libddb gives one (the
/// extension's otherwise), file validators (<c>ETag</c>, 304, <c>Range</c> answered 206) and
/// <c>Cache-Control: private, no-cache</c>, or <c>private, max-age=&lt;bounded&gt;, immutable</c> when the URL names
/// the current version of the file's artifact (<c>build/{hash}@{version}/{path}</c>), so a browser reads a file it
/// streams by ranges (a splat's <c>.rad</c>) from its cache without revalidating every range.</item>
/// <item><c>GET build-artifact</c> serves the manifest's artifact and download name with the media type
/// Registry has always sent (the switch to the manifest's media types is a pending decision).</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~BuildFilesHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class BuildFilesHttpTests : IDisposable
{
    private const string OwnerUser = "buildfileshttp-owner";
    private const string Org = "buildfileshttp-org";
    private const string Ds = "buildfileshttp-ds";
    private const string Hash = "d0d2b869615dca09ef0055f2014401bab298503e3e1f10e339f14f5788ea702f";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private string _datasetFolder = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(OwnerUser);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        var ds = Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
        _datasetFolder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(Org, ds.InternalRef).DatasetFolderPath;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void ResetDdb()
    {
        _factory.DdbWrapper.Invocations.Clear();
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((ResolvedBuildFile?)null);
    }

    // The build folder of the entry, where libddb (and the old composed path) has its files
    private string BuildFile(string relative, string content)
    {
        var path = Path.Combine(_datasetFolder, ".ddb", "build", Hash, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, url).AsUser(OwnerUser);
        configure?.Invoke(request);
        return await _client.SendAsync(request);
    }

    private string Resolves(string relative, string content, string? mediaType = null, string? version = null)
    {
        var file = BuildFile(relative, content);
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(_datasetFolder, Hash, relative))
            .Returns(new ResolvedBuildFile(file, mediaType, version));
        return file;
    }

    private static string BuildFileUrl(string relative) => $"/orgs/{Org}/ds/{Ds}/build/{Hash}/{relative}";

    // The URL of a build file at a version of its artifact, as the artifact links of an entry name it
    private static string VersionedBuildFileUrl(string version, string relative) =>
        $"/orgs/{Org}/ds/{Ds}/build/{Hash}@{version}/{relative}";

    private static readonly int VersionedMaxAgeSeconds = (int)HashedContentCachePolicy.VersionedMaxAge.TotalSeconds;

    private static void ShouldBeImmutable(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl!;
        cacheControl.Private.ShouldBeTrue("the file sits behind dataset authorization");
        cacheControl.NoCache.ShouldBeFalse();
        cacheControl.MaxAge.ShouldBe(TimeSpan.FromSeconds(VersionedMaxAgeSeconds));
        cacheControl.Extensions.Select(e => e.Name).ShouldContain("immutable");
    }

    private static void ShouldBeRevalidated(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl!;
        cacheControl.Private.ShouldBeTrue();
        cacheControl.NoCache.ShouldBeTrue();
        cacheControl.ToString().ShouldNotContain("immutable");
        cacheControl.MaxAge.ShouldBeNull();
    }

    [Test]
    public async Task BuildFile_NotPartOfTheBuild_Is404NotA500()
    {
        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build/{Hash}/cog/missing.tif");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task BuildFile_OfTheBuild_IsServedWithTheMediaTypeOfItsExtension()
    {
        var file = BuildFile("3dtiles/tileset.json", """{"asset":{"version":"1.0"}}""");
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(_datasetFolder, Hash, "3dtiles/tileset.json")).Returns(new ResolvedBuildFile(file));

        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build/{Hash}/3dtiles/tileset.json");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        (await response.Content.ReadAsStringAsync()).ShouldBe("""{"asset":{"version":"1.0"}}""");
    }

    [Test]
    public async Task BuildFile_Head_AnswersWhetherTheFileIsPartOfTheBuild()
    {
        var file = BuildFile("cog/cog.tif", "tiff");
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(_datasetFolder, Hash, "cog/cog.tif")).Returns(new ResolvedBuildFile(file));

        using var present = await SendAsync(HttpMethod.Head, $"/orgs/{Org}/ds/{Ds}/build/{Hash}/cog/cog.tif");
        using var absent = await SendAsync(HttpMethod.Head, $"/orgs/{Org}/ds/{Ds}/build/{Hash}/cog/other.tif");

        present.StatusCode.ShouldBe(HttpStatusCode.OK);
        absent.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The end-to-end check of Registry eb1311f2: HEAD answered 200 with no Content-Length, Content-Type or
    // Accept-Ranges (a client sizing a download or probing for range support learned nothing). HEAD is GET without
    // the body now: the same file result, so the same headers.
    [Test]
    public async Task BuildFile_Head_SendsTheHeadersOfGet_WithoutABody()
    {
        var file = BuildFile("cog/sized.tif", "0123456789");
        _factory.DdbWrapper.Setup(d => d.ResolveBuildFile(_datasetFolder, Hash, "cog/sized.tif")).Returns(new ResolvedBuildFile(file));
        var url = $"/orgs/{Org}/ds/{Ds}/build/{Hash}/cog/sized.tif";

        using var get = await SendAsync(HttpMethod.Get, url);
        using var head = await SendAsync(HttpMethod.Head, url);

        head.StatusCode.ShouldBe(HttpStatusCode.OK);
        head.Content.Headers.ContentLength.ShouldBe(10);
        head.Content.Headers.ContentType!.MediaType.ShouldBe(get.Content.Headers.ContentType!.MediaType);
        head.Headers.AcceptRanges.ShouldContain("bytes");
        head.Headers.AcceptRanges.ShouldBe(get.Headers.AcceptRanges);
        head.Content.Headers.LastModified.ShouldBe(get.Content.Headers.LastModified);
        head.Headers.ETag.ShouldBe(get.Headers.ETag);
        (await head.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    // The manifest knows what an artifact is; the extension only guesses (.rad and .spz are no extension a MIME table
    // knows). libddb gives the type of a declared artifact when it can, and the extension answers when it does not
    [Test]
    public async Task BuildFile_WithAMediaTypeFromLibddb_IsServedWithIt_NotWithTheOneOfItsExtension()
    {
        Resolves("cog/cog.tif", "tiff", "image/tiff; application=geotiff; profile=cloud-optimized");

        using var response = await SendAsync(HttpMethod.Get, BuildFileUrl("cog/cog.tif"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe("image/tiff; application=geotiff; profile=cloud-optimized");
    }

    [Test]
    public async Task BuildFile_WithoutAMediaTypeFromLibddb_FallsBackToTheOneOfItsExtension()
    {
        Resolves("cog/cog.tif", "tiff");

        using var response = await SendAsync(HttpMethod.Get, BuildFileUrl("cog/cog.tif"));

        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/tiff");
    }

    [Test]
    public async Task BuildFile_Rad_AnswersARangeRequestWith206AndOnlyThoseBytes()
    {
        Resolves("gsplat/model.rad", "0123456789", "application/octet-stream");

        using var response = await SendAsync(HttpMethod.Get, BuildFileUrl("gsplat/model.rad"),
            r => r.Headers.Range = new RangeHeaderValue(2, 5));

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().ShouldBe("bytes 2-5/10");
        (await response.Content.ReadAsStringAsync()).ShouldBe("2345");
    }

    // A URL without a version names whatever the build holds at that path: a forced rebuild rewrites the file under
    // the same hash, so the client may keep it but must ask before it reuses it (no-cache), whatever version libddb
    // reports
    [TestCase("GET")]
    [TestCase("HEAD")]
    public async Task BuildFile_WithoutAVersion_CanBeKept_ButMustBeRevalidated(string method)
    {
        Resolves("gsplat/model.rad", "0123456789", version: "3f2a");

        using var response = await SendAsync(new HttpMethod(method), BuildFileUrl("gsplat/model.rad"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldBeRevalidated(response);
        response.Headers.ETag.ShouldNotBeNull();
        response.Headers.ETag!.IsWeak.ShouldBeFalse();
        response.Content.Headers.LastModified.ShouldNotBeNull();
    }

    // The viewer streams a .rad by ranges. A URL that names the current version of the artifact identifies its bytes,
    // so the browser keeps every range without revalidating it (on revisits Chromium revalidated each range, and
    // sometimes answered one range with the whole cached file); libddb resolves the file by its path alone
    [TestCase("GET")]
    [TestCase("HEAD")]
    public async Task BuildFile_AtTheCurrentVersionOfItsArtifact_IsImmutable_ForTheBoundedMaxAge(string method)
    {
        Resolves("gsplat/model.rad", "0123456789", "application/octet-stream", version: "3f2a");

        using var response = await SendAsync(new HttpMethod(method), VersionedBuildFileUrl("3f2a", "gsplat/model.rad"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldBeImmutable(response);
        response.Headers.ETag!.IsWeak.ShouldBeFalse();
        response.Content.Headers.ContentLength.ShouldBe(10);
        _factory.DdbWrapper.Verify(d => d.ResolveBuildFile(_datasetFolder, Hash, "gsplat/model.rad"), Times.Once);
    }

    [Test]
    public async Task BuildFile_AtTheCurrentVersion_AnswersARangeRequestWith206_ThatIsImmutable()
    {
        Resolves("gsplat/model.rad", "0123456789", "application/octet-stream", version: "3f2a");

        using var response = await SendAsync(HttpMethod.Get, VersionedBuildFileUrl("3f2a", "gsplat/model.rad"),
            r => r.Headers.Range = new RangeHeaderValue(2, 5));

        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().ShouldBe("bytes 2-5/10");
        (await response.Content.ReadAsStringAsync()).ShouldBe("2345");
        ShouldBeImmutable(response);
    }

    // A rebuild gave the artifact a new version: a URL with the old one (a page opened before the rebuild) still gets
    // the file, but as one that can change, revalidated (304 while it is the same)
    [Test]
    public async Task BuildFile_AtAVersionARebuildReplaced_MustBeRevalidated_AndIs304WhileUnchanged()
    {
        Resolves("gsplat/model.rad", "0123456789", version: "b07d");
        using var first = await SendAsync(HttpMethod.Get, VersionedBuildFileUrl("3f2a", "gsplat/model.rad"));

        using var again = await SendAsync(HttpMethod.Get, VersionedBuildFileUrl("3f2a", "gsplat/model.rad"),
            r => r.Headers.IfNoneMatch.Add(first.Headers.ETag!));

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldBeRevalidated(first);
        (await first.Content.ReadAsStringAsync()).ShouldBe("0123456789");
        again.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        again.Headers.ETag.ShouldBe(first.Headers.ETag);
        ShouldBeRevalidated(again);
    }

    // A libddb that reports no version cannot vouch for any: a version in the URL changes nothing
    [Test]
    public async Task BuildFile_WhoseArtifactHasNoVersion_IsNeverImmutable()
    {
        Resolves("gsplat/model.rad", "0123456789");

        using var response = await SendAsync(HttpMethod.Get, VersionedBuildFileUrl("3f2a", "gsplat/model.rad"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        ShouldBeRevalidated(response);
    }

    // The version is in the path, so the files a tileset.json names by relative URL carry it: libddb gives every file
    // of a directory artifact the version of its index, and the tiles are kept like the index. The JSON index keeps
    // its weak tag (the response compression encodes it)
    [Test]
    public async Task BuildFile_OfADirectoryArtifact_AtItsVersion_IsImmutable_IndexAndTilesAlike()
    {
        Resolves("3dtiles/tileset.json", Tileset, "application/json", version: "77c1");
        Resolves("3dtiles/tiles/0.b3dm", "b3dm bytes", version: "77c1");

        using var index = await SendAsync(HttpMethod.Get, VersionedBuildFileUrl("77c1", "3dtiles/tileset.json"),
            r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
        using var tile = await SendAsync(HttpMethod.Get, VersionedBuildFileUrl("77c1", "3dtiles/tiles/0.b3dm"));

        index.StatusCode.ShouldBe(HttpStatusCode.OK);
        index.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);
        index.Headers.ETag!.IsWeak.ShouldBeTrue();
        ShouldBeImmutable(index);
        tile.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await tile.Content.ReadAsStringAsync()).ShouldBe("b3dm bytes");
        ShouldBeImmutable(tile);
    }

    [Test]
    public async Task BuildFile_NotPartOfTheBuild_AtAVersion_Is404()
    {
        using var get = await SendAsync(HttpMethod.Get, VersionedBuildFileUrl("3f2a", "cog/missing.tif"));
        using var head = await SendAsync(HttpMethod.Head, VersionedBuildFileUrl("3f2a", "cog/missing.tif"));

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        head.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [TestCase("GET")]
    [TestCase("HEAD")]
    public async Task BuildFile_WhoseEtagTheClientHas_Is304_WithoutABody(string method)
    {
        Resolves("gsplat/model.rad", "0123456789");
        using var first = await SendAsync(HttpMethod.Get, BuildFileUrl("gsplat/model.rad"));
        var etag = first.Headers.ETag!;

        using var response = await SendAsync(new HttpMethod(method), BuildFileUrl("gsplat/model.rad"),
            r => r.Headers.IfNoneMatch.Add(etag));

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Headers.ETag.ShouldBe(etag);
        ShouldBeRevalidated(response);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBeEmpty();
    }

    // A forced rebuild rewrites the file and keeps the build hash: the validator must follow the file
    [Test]
    public async Task BuildFile_Rewritten_HasANewEtag_AndTheOldOneIsNoLongerCurrent()
    {
        Resolves("gsplat/model.rad", "0123456789");
        using var before = await SendAsync(HttpMethod.Get, BuildFileUrl("gsplat/model.rad"));
        var oldEtag = before.Headers.ETag!;

        Resolves("gsplat/model.rad", "rebuilt with more bytes");
        using var after = await SendAsync(HttpMethod.Get, BuildFileUrl("gsplat/model.rad"),
            r => r.Headers.IfNoneMatch.Add(oldEtag));

        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        after.Headers.ETag.ShouldNotBe(oldEtag);
        (await after.Content.ReadAsStringAsync()).ShouldBe("rebuilt with more bytes");
    }

    // A rebuild that writes as many bytes as before still moves the write time: the tag must follow the time too,
    // not the length alone
    [Test]
    public async Task BuildFile_RewrittenWithTheSameLength_HasANewEtag()
    {
        var written = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Resolves("gsplat/model.rad", "0123456789"), written);
        using var before = await SendAsync(HttpMethod.Get, BuildFileUrl("gsplat/model.rad"));
        var oldEtag = before.Headers.ETag!;

        File.SetLastWriteTimeUtc(Resolves("gsplat/model.rad", "9876543210"), written.AddMinutes(1));
        using var after = await SendAsync(HttpMethod.Get, BuildFileUrl("gsplat/model.rad"),
            r => r.Headers.IfNoneMatch.Add(oldEtag));

        after.StatusCode.ShouldBe(HttpStatusCode.OK);
        after.Headers.ETag.ShouldNotBe(oldEtag);
        (await after.Content.ReadAsStringAsync()).ShouldBe("9876543210");
    }

    private const string Tileset = """{"asset":{"version":"1.0"},"geometricError":500}""";

    // The response compression gzips a JSON build file (tileset.json, bounds.json) for a client that accepts it and
    // keeps the ETag it was given: the gzip and the identity bytes are two representations, so one strong tag must
    // not name both. The tag is weak for such a type, whichever encoding the client asked for
    [Test]
    public async Task BuildFile_OfATypeTheCompressionEncodes_HasAWeakEtag_ForEveryEncoding()
    {
        Resolves("3dtiles/tileset.json", Tileset, "application/json");

        using var gzip = await SendAsync(HttpMethod.Get, BuildFileUrl("3dtiles/tileset.json"),
            r => r.Headers.AcceptEncoding.ParseAdd("gzip"));
        using var identity = await SendAsync(HttpMethod.Get, BuildFileUrl("3dtiles/tileset.json"));

        gzip.Content.Headers.ContentEncoding.ShouldBe(["gzip"]);
        gzip.Headers.ETag!.IsWeak.ShouldBeTrue();
        identity.Content.Headers.ContentEncoding.ShouldBeEmpty();
        identity.Headers.ETag.ShouldBe(gzip.Headers.ETag);
        (await identity.Content.ReadAsStringAsync()).ShouldBe(Tileset);
    }

    // If-Range compares strongly: with the tag of a gzip response it must not return a range of the identity bytes,
    // which the client would join to the gzip ones it has; it sends the whole file instead
    [Test]
    public async Task BuildFile_OfATypeTheCompressionEncodes_IfRangeWithItsTag_SendsTheWholeFile()
    {
        Resolves("3dtiles/tileset.json", Tileset, "application/json");
        using var gzip = await SendAsync(HttpMethod.Get, BuildFileUrl("3dtiles/tileset.json"),
            r => r.Headers.AcceptEncoding.ParseAdd("gzip"));

        using var response = await SendAsync(HttpMethod.Get, BuildFileUrl("3dtiles/tileset.json"), r =>
        {
            r.Headers.Range = new RangeHeaderValue(2, 5);
            r.Headers.IfRange = new RangeConditionHeaderValue(gzip.Headers.ETag!);
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe(Tileset);
    }

    [Test]
    public async Task BuildFile_OfATypeTheCompressionEncodes_WhoseEtagTheClientHas_Is304()
    {
        Resolves("3dtiles/tileset.json", Tileset, "application/json");
        using var gzip = await SendAsync(HttpMethod.Get, BuildFileUrl("3dtiles/tileset.json"),
            r => r.Headers.AcceptEncoding.ParseAdd("gzip"));

        using var response = await SendAsync(HttpMethod.Get, BuildFileUrl("3dtiles/tileset.json"), r =>
        {
            r.Headers.AcceptEncoding.ParseAdd("gzip");
            r.Headers.IfNoneMatch.Add(gzip.Headers.ETag!);
        });

        response.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        response.Headers.ETag.ShouldBe(gzip.Headers.ETag);
    }

    // A .rad is never encoded: its tag stays strong for a client that accepts gzip, so the viewer resumes it by
    // ranges with If-Range
    [Test]
    public async Task BuildFile_Rad_KeepsAStrongEtag_AndIfRangeWithItAnswers206()
    {
        Resolves("gsplat/model.rad", "0123456789", "application/octet-stream");
        using var first = await SendAsync(HttpMethod.Get, BuildFileUrl("gsplat/model.rad"),
            r => r.Headers.AcceptEncoding.ParseAdd("gzip"));

        using var response = await SendAsync(HttpMethod.Get, BuildFileUrl("gsplat/model.rad"), r =>
        {
            r.Headers.AcceptEncoding.ParseAdd("gzip");
            r.Headers.Range = new RangeHeaderValue(2, 5);
            r.Headers.IfRange = new RangeConditionHeaderValue(first.Headers.ETag!);
        });

        first.Content.Headers.ContentEncoding.ShouldBeEmpty();
        first.Headers.ETag!.IsWeak.ShouldBeFalse();
        response.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        (await response.Content.ReadAsStringAsync()).ShouldBe("2345");
    }

    private static IEnumerable<TestCaseData> Downloads()
    {
        yield return new TestCaseData("ortho.tif", EntryType.GeoRaster, ArtifactRoles.RasterCog, "cog/cog.tif",
            "image/tiff; application=geotiff; profile=cloud-optimized", "cog.tif").SetName("{m}(cog)");
        yield return new TestCaseData("cloud.laz", EntryType.PointCloud, ArtifactRoles.PointCloudCopc,
            "copc/cloud.copc.laz", "application/vnd.laszip+copc", "cloud.copc.laz")
            .SetName("{m}(copc)");
        yield return new TestCaseData("roads.geojson", EntryType.Vector, ArtifactRoles.VectorGpkg, "vec/source.gpkg",
            "application/geopackage+sqlite3", "source.gpkg").SetName("{m}(gpkg)");
    }

    [TestCaseSource(nameof(Downloads))]
    public async Task BuildArtifact_IsTheManifestsDownload_WithTheManifestsMediaType(string path, EntryType type,
        string role, string artifactPath, string manifestType, string downloadName)
    {
        BuildFile(artifactPath, "artifact bytes");
        SetupEntry(path, type, BuildState.Complete,
            TestBuilds.File(role, artifactPath, manifestType, downloadName));

        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build-artifact?path={path}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.ToString().ShouldBe(manifestType);
        response.Content.Headers.ContentDisposition!.FileName.ShouldBe(downloadName);
        (await response.Content.ReadAsStringAsync()).ShouldBe("artifact bytes");
    }

    [Test]
    public async Task BuildArtifact_NotBuiltYet_Is404()
    {
        SetupEntry("late.tif", EntryType.GeoRaster, BuildState.None, hash: new string('7', 64));

        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build-artifact?path=late.tif");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("may not have been built yet");
    }

    [Test]
    public async Task BuildArtifact_OfAnEntryWithoutDownload_Is400()
    {
        _factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, "photo.jpg"))
            .Returns(new Entry { Path = "photo.jpg", Hash = "1234", Type = EntryType.Image, Size = 1 });
        _factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == "photo.jpg")))
            .Returns([TestBuilds.NotBuildable("photo.jpg", EntryType.Image)]);

        using var response = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/{Ds}/build-artifact?path=photo.jpg");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("does not have a downloadable build artifact");
    }

    private void SetupEntry(string path, EntryType type, BuildState state, params BuildArtifact[] artifacts) =>
        SetupEntry(path, type, state, Hash, artifacts);

    private void SetupEntry(string path, EntryType type, BuildState state, string hash, params BuildArtifact[] artifacts)
    {
        _factory.DdbWrapper.Setup(d => d.GetEntry(_datasetFolder, path))
            .Returns(new Entry { Path = path, Hash = hash, Type = type, Size = 1 });
        _factory.DdbWrapper.Setup(d => d.GetBuildArtifacts(_datasetFolder, It.Is<string[]>(p => p.Single() == path)))
            .Returns([TestBuilds.Build(_datasetFolder, path, type, hash, state, artifacts)]);
    }
}
