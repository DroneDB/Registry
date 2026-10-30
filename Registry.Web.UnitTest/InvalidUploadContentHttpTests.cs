using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
/// Through HTTP on <see cref="RegistryWebFactory"/>: an uploaded file that libddb's reader refuses (a
/// <c>.laz</c> that is not LAZ, a raster GDAL cannot open, malformed JSON) is a 400 ProblemDetails with
/// <c>noRetry</c> that names the file, not a 500; a file the server could not read stays a server fault.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~InvalidUploadContentHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class InvalidUploadContentHttpTests : IDisposable
{
    private const string OwnerUser = "invalidcontent-owner";
    private const string Org = "invalidcontent-org";
    private const string Ds = "invalidcontent-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

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
        Seed.Dataset(context, org, Ds);
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    // What libddb reports for each reader (the message of PDAL is the one of a .laz that is not LAZ)
    [TestCase("cloud.laz", BatchAddItemError.Codes.Pdal,
        "readers.las: Invalid file signature. Was expecting 'LASF', Check the first four  bytes of the file.")]
    [TestCase("ortho.tif", BatchAddItemError.Codes.Gdal, "not recognized as being in a supported file format")]
    [TestCase("shapes.geojson", BatchAddItemError.Codes.Json, "parse error")]
    public async Task Upload_TheReaderRefusesTheFile_Is400NamingTheFile(string file, string code, string message)
    {
        NativeAddFails(file, code, message);

        var (response, problem) = await UploadAsync(file);

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status400BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        problem.ShouldNotBeNull();
        problem["detail"]!.GetValue<string>().ShouldBe($"'{file}' cannot be read as its file type: {message}");
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task Upload_TheServerCannotReadTheFile_StaysAServerFault()
    {
        NativeAddFails("lost.laz", BatchAddItemError.Codes.FileSystem, "No such file or directory");

        var (response, _) = await UploadAsync("lost.laz");

        ((int)response.StatusCode).ShouldBe(StatusCodes.Status500InternalServerError);
    }

    private void NativeAddFails(string file, string code, string message) =>
        _factory.DdbWrapper
            .Setup(d => d.AddWithHints(It.IsAny<string>(), It.IsAny<IReadOnlyList<AddItem>>(), It.IsAny<bool>()))
            .Returns((string _, IReadOnlyList<AddItem> items, bool _) => new BatchAddResult
            {
                Errors = items.Select(_ => new BatchAddItemError { Path = file, Code = code, Message = message }).ToList()
            });

    private async Task<(HttpResponseMessage Response, JsonObject? Problem)> UploadAsync(string path)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(path), "path");
        var file = new ByteArrayContent([1, 2, 3, 4]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", path);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/obj") { Content = content }
            .AsUser(OwnerUser);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        return (response, body.TrimStart().StartsWith('{') ? JsonNode.Parse(body) as JsonObject : null);
    }
}
