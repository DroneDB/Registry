#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Through HTTP on <see cref="RegistryWebFactory"/>: the dataset path rule at the entry points
/// is libddb's (<c>DDBNormalizePath</c>, golden vectors) and every refused path is one 400
/// ProblemDetails with <c>noRetry</c>.
/// <list type="bullet">
/// <item>Windows drive, UNC and device paths are absolute on every OS: <c>Path.IsPathRooted</c>
/// accepted them on Linux (an upload wrote a file named <c>C:\x.txt</c>).</item>
/// <item>A reserved upload path was a 500 (<c>InvalidOperationException</c>).</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~PathRuleHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class PathRuleHttpTests : IDisposable
{
    private const string OwnerUser = "pathrulehttp-owner";
    private const string Org = "pathrulehttp-org";
    private const string Ds = "pathrulehttp-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new Registry.Ports.DroneDB.DdbException("Key not found"));
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

    [TestCase(@"C:\x.txt")]
    [TestCase("c:/x.txt")]
    [TestCase("C:x.txt")]
    [TestCase(@"\\server\share\x.txt")]
    [TestCase("//server/share/x.txt")]
    [TestCase(@"\\?\C:\x.txt")]
    [TestCase("/etc/x.txt")]
    public async Task Upload_AbsolutePath_Is400BeforeTheBodyIsStored(string path)
    {
        var (response, problem) = await UploadAsync(path);

        ShouldBe400(response, problem);
        problem["detail"]!.GetValue<string>().ShouldContain("absolute paths are not allowed");
    }

    [TestCase(".ddb/x.txt")]
    [TestCase("sub/.ddb/x.txt")]
    [TestCase(".uploads/x.txt")]
    [TestCase(@".uploads\quarantine\x.txt")]
    public async Task Upload_ReservedPath_Is400NotA500(string path)
    {
        var (response, problem) = await UploadAsync(path);

        ShouldBe400(response, problem);
        problem["detail"]!.GetValue<string>().ShouldContain("is a reserved path");
    }

    [TestCase("../x.txt")]
    [TestCase(@"a\..\..\x.txt")]
    public async Task Upload_Traversal_Is400(string path)
    {
        var (response, problem) = await UploadAsync(path);

        ShouldBe400(response, problem);
        problem["detail"]!.GetValue<string>().ShouldContain("path traversal is not allowed");
    }

    // The controllers' early check is the same rule (the model binder applies it too)
    [TestCase(@"C:\ortho.tif")]
    [TestCase(@"\\server\share\ortho.tif")]
    public async Task RasterInfo_WindowsAbsolutePath_Is400Absolute(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/orgs/{Org}/ds/{Ds}/raster-info?path={Uri.EscapeDataString(path)}").AsUser(OwnerUser);

        var (response, problem) = await SendAsync(request);

        ShouldBe400(response, problem);
        problem["detail"]!.GetValue<string>().ShouldContain("absolute paths are not allowed");
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Problem)> UploadAsync(string path)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(path), "path");
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", "x.txt");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/obj") { Content = content }
            .AsUser(OwnerUser);
        return await SendAsync(request);
    }

    private async Task<(HttpResponseMessage Response, JsonObject? Problem)> SendAsync(HttpRequestMessage request)
    {
        using (request)
        {
            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            return (response, body.TrimStart().StartsWith('{') ? JsonNode.Parse(body) as JsonObject : null);
        }
    }

    private static void ShouldBe400(HttpResponseMessage response, [NotNull] JsonObject? problem)
    {
        ((int)response.StatusCode).ShouldBe(StatusCodes.Status400BadRequest);
        problem.ShouldNotBeNull();
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }
}
