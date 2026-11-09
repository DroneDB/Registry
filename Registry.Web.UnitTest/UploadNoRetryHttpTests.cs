using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
/// FR-XC-12, FR-UPL-07 through HTTP: the upload endpoints answer 403, 404 and a final 409 with <c>noRetry: true</c>, and
/// a commit with missing chunks (recoverable) with <c>noRetry: false</c>; a 404 of another endpoint keeps
/// <c>noRetry: false</c>.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UploadNoRetryHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class UploadNoRetryHttpTests : IDisposable
{
    private const string Owner = "noretry-owner";
    private const string Stranger = "noretry-stranger";
    private const string Org = "noretry-org";
    private const string Ds = "noretry-ds";

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));
        // An empty index: the name rule of an upload (D-040) lists the target folder
        _factory.DdbWrapper.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.Is<EntryQuery>(q => !q.Recursive)))
            .Returns([]);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        await _factory.CreateUserAsync(Stranger);
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

    private async Task<(HttpStatusCode Status, bool? NoRetry)> SendAsync(HttpMethod method, string url, string user,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content }.AsUser(user);
        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var noRetry = body.TrimStart().StartsWith('{') ? JsonNode.Parse(body)?["noRetry"]?.GetValue<bool>() : null;
        return (response.StatusCode, noRetry);
    }

    private static MultipartFormDataContent File(string path)
    {
        var content = new MultipartFormDataContent { { new StringContent(path), "path" } };
        var file = new ByteArrayContent([1, 2, 3, 4]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", path);
        return content;
    }

    private static MultipartFormDataContent Chunk(string fileId, string path, int index, int total)
    {
        var content = new MultipartFormDataContent
        {
            { new StringContent(fileId), "fileId" },
            { new StringContent(index.ToString(CultureInfo.InvariantCulture)), "chunkIndex" },
            { new StringContent(total.ToString(CultureInfo.InvariantCulture)), "totalChunks" },
            { new StringContent((4 * total).ToString(CultureInfo.InvariantCulture)), "totalFileSize" },
            { new StringContent("4"), "chunkSize" },
            { new StringContent(path), "path" }
        };
        var chunk = new ByteArrayContent([1, 2, 3, 4]);
        chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(chunk, "chunk", "blob");
        return content;
    }

    [Test]
    public async Task Upload_ToADatasetThatDoesNotExist_Is404_NoRetry()
    {
        var (status, noRetry) = await SendAsync(HttpMethod.Post, $"/orgs/{Org}/ds/missing-ds/obj", Owner, File("a.txt"));

        status.ShouldBe(HttpStatusCode.NotFound);
        noRetry.ShouldBe(true);
    }

    [Test]
    public async Task Upload_WithoutWriteAccess_Is403_NoRetry()
    {
        var (status, noRetry) = await SendAsync(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/obj", Stranger, File("a.txt"));

        status.ShouldBe(HttpStatusCode.Forbidden);
        noRetry.ShouldBe(true);
    }

    [Test]
    public async Task Commit_OfAnUnknownUpload_Is404_NoRetry()
    {
        var (status, noRetry) = await SendAsync(HttpMethod.Post,
            $"/orgs/{Org}/ds/{Ds}/obj/chunk/unknown-upload-0001/commit", Owner);

        status.ShouldBe(HttpStatusCode.NotFound);
        noRetry.ShouldBe(true);
    }

    [Test]
    public async Task Commit_WithMissingChunks_Is409_AndStaysRetryable()
    {
        var (stored, _) = await SendAsync(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/obj/chunk", Owner,
            Chunk("missing-chunks-0001", "big.bin", 0, 2));
        stored.ShouldBe(HttpStatusCode.OK);

        var (status, noRetry) = await SendAsync(HttpMethod.Post,
            $"/orgs/{Org}/ds/{Ds}/obj/chunk/missing-chunks-0001/commit", Owner);

        status.ShouldBe(HttpStatusCode.Conflict);
        noRetry.ShouldBe(false);
    }

    [Test]
    public async Task Chunk_OfAnUploadWithAnotherFile_Is409_NoRetry()
    {
        var (stored, _) = await SendAsync(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/obj/chunk", Owner,
            Chunk("other-file-0001", "one.bin", 0, 2));
        stored.ShouldBe(HttpStatusCode.OK);

        var (status, noRetry) = await SendAsync(HttpMethod.Post, $"/orgs/{Org}/ds/{Ds}/obj/chunk", Owner,
            Chunk("other-file-0001", "two.bin", 1, 2));

        status.ShouldBe(HttpStatusCode.Conflict);
        noRetry.ShouldBe(true);
    }

    // The rule is the upload answers': the same 404 elsewhere keeps the classifier's flag
    [Test]
    public async Task A404OfAnotherEndpoint_KeepsNoRetryFalse()
    {
        var (status, noRetry) = await SendAsync(HttpMethod.Get, $"/orgs/{Org}/ds/missing-ds", Owner);

        status.ShouldBe(HttpStatusCode.NotFound);
        noRetry.ShouldBe(false);
    }
}
