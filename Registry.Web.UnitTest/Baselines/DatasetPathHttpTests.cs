using System;
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

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/>: the request paths of the raster, stockpile and
/// build-artifact endpoints are a <c>DatasetPath</c>, bound once by libddb's rule (a model binder for the query,
/// a JSON converter for bodies) instead of 20 hand-written checks. The responses are the ones the checks gave:
/// a refused path is one 400 ProblemDetails with <c>noRetry</c> and the rule, before the access check; a missing
/// or blank path is the model validation's "required" 400; a valid path reaches the service normalized.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetPathHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class DatasetPathHttpTests : IDisposable
{
    private const string OwnerUser = "datasetpathhttp-path-owner";
    private const string Org = "datasetpathhttp-path-org";
    private const string Ds = "datasetpathhttp-path-ds";
    private const string Api = $"/orgs/{Org}/ds/{Ds}";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // No entry at any path: a request that reaches its service is refused there
        _factory.DdbWrapper.Setup(d => d.GetEntry(It.IsAny<string>(), It.IsAny<string>())).Returns((Entry?)null);
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

    private async Task<(int Status, JsonObject Problem)> SendAsync(HttpMethod method, string url, string? json = null,
        bool anonymous = false)
    {
        using var request = new HttpRequestMessage(method, url);
        if (json != null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (!anonymous)
            request.AsUser(OwnerUser);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, (JsonNode.Parse(body) as JsonObject)!);
    }

    private static void ShouldBeRefusedPath(int status, JsonObject problem, string detail)
    {
        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
        problem["detail"]!.GetValue<string>().ShouldBe(detail);
    }

    private static void ShouldBeRequired(int status, JsonObject problem, string field)
    {
        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem["noRetry"]!.GetValue<bool>().ShouldBeFalse();
        problem["detail"]!.GetValue<string>().ShouldBe($"The {field} field is required.");
    }

    [TestCase("raster-info")]
    [TestCase("raster-metadata")]
    [TestCase("raster-value-info")]
    [TestCase("raster-point-value", "&x=1&y=2")]
    [TestCase("raster-area-stats", "&x0=0&y0=0&x1=1&y1=1")]
    [TestCase("thumb-ex")]
    [TestCase("tiles-ex/1/2/3.png")]
    [TestCase("export")]
    [TestCase("build-artifact")]
    public async Task QueryPath_Refused_IsThePathRule400(string action, string extra = "")
    {
        var (status, problem) = await SendAsync(HttpMethod.Get, $"{Api}/{action}?path=..%2Fx.tif{extra}");

        ShouldBeRefusedPath(status, problem, "Invalid path '../x.tif': path traversal is not allowed.");
    }

    [Test]
    public async Task QueryPath_Refused_BeforeTheAccessCheck()
    {
        var (status, problem) = await SendAsync(HttpMethod.Get, $"{Api}/raster-info?path=%2Fetc%2Fx.tif", anonymous: true);

        ShouldBeRefusedPath(status, problem, "Invalid path '/etc/x.tif': absolute paths are not allowed.");
    }

    [TestCase("")]
    [TestCase("?path=")]
    [TestCase("?path=%20")]
    public async Task QueryPath_MissingOrBlank_IsRequired(string query)
    {
        var (status, problem) = await SendAsync(HttpMethod.Get, $"{Api}/raster-info{query}");

        ShouldBeRequired(status, problem, "path");
    }

    [Test]
    public async Task QueryPath_Valid_ReachesTheServiceNormalized()
    {
        var (status, problem) = await SendAsync(HttpMethod.Get, $"{Api}/raster-info?path=.%2Fsub%2F%2Fx.tif");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem["detail"]!.GetValue<string>().ShouldBe("Invalid path: 'sub/x.tif'");
    }

    [TestCase("stockpile/calculate", """, "polygon": "{}" """)]
    [TestCase("stockpile/detect", """, "lat": 1, "lon": 2 """)]
    [TestCase("stockpile/detect-all", "")]
    [TestCase("raster-profile", """, "lineString": "{}" """)]
    [TestCase("contours", """, "interval": 1 """)]
    [TestCase("mask-borders", "")]
    [TestCase("mask-borders/check", "")]
    public async Task BodyPath_Refused_IsThePathRule400(string action, string rest)
    {
        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Api}/{action}",
            $$"""{ "path": "C:\\dem.tif" {{rest}} }""");

        ShouldBeRefusedPath(status, problem, @"Invalid path 'C:\dem.tif': absolute paths are not allowed.");
    }

    [TestCase("""{ "polygon": "{}" }""")]
    [TestCase("""{ "path": "", "polygon": "{}" }""")]
    [TestCase("""{ "path": null, "polygon": "{}" }""")]
    public async Task BodyPath_MissingOrBlank_IsRequired(string json)
    {
        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Api}/stockpile/calculate", json);

        ShouldBeRequired(status, problem, "Path");
    }

    [Test]
    public async Task BodyPath_NotAString_IsAModelError()
    {
        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Api}/stockpile/calculate",
            """{ "path": { "a": 1 }, "polygon": "{}" }""");

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem["noRetry"]!.GetValue<bool>().ShouldBeFalse();
    }

    // The merge and align bodies name several paths: the rule's message names them "path" (it said "input path")
    [TestCase("merge-multispectral/validate", """{ "paths": ["a.tif", "../b.tif"] }""", "'../b.tif': path traversal")]
    [TestCase("merge-multispectral", """{ "paths": ["a.tif"], "outputPath": "/out.tif" }""", "'/out.tif': absolute paths")]
    [TestCase("align/validate", """{ "sourcePath": "a.tif", "referencePath": "..\\b.tif" }""", @"'..\b.tif': path traversal")]
    public async Task BodyPaths_Refused_IsThePathRule400(string action, string json, string detail)
    {
        var (status, problem) = await SendAsync(HttpMethod.Post, $"{Api}/{action}", json);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
        problem["detail"]!.GetValue<string>().ShouldContain(detail);
    }
}
