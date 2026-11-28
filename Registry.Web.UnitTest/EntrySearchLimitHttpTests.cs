using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Test.Baselines;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The optional <c>limit</c> form field of <c>POST .../search</c>: the Hub asks whether a type has any entry, and must
/// not receive (nor have converted and annotated) every one of a dataset of thousands. Absent, the answer is the whole
/// one; present, its first entries in the same order; 0, a negative or a non-numeric value is a 400.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~EntrySearchLimitHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class EntrySearchLimitHttpTests : DatasetAccessFixture
{
    private const string Search = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs + "/search";

    private static readonly string[] Paths = ["a.txt", "b.jpg", "c.jpg", "d.jpg", "e.png"];

    [OneTimeSetUp]
    public async Task IndexTheEntries()
    {
        var internalRef = await WithContextAsync(context =>
            Task.FromResult(context.Datasets.Single(d => d.Slug == PrivateDs).InternalRef));
        using var scope = Factory.Services.CreateScope();
        var folder = scope.ServiceProvider.GetRequiredService<IDdbManager>().Get(PrivateOrg, internalRef)
            .DatasetFolderPath;
        // libddb answers the whole folder: the name check of the Registry is what selects by name
        Factory.DdbWrapper.Setup(d => d.ListEntries(folder, It.IsAny<EntryQuery>())).Returns(Paths
            .Select(path => new Entry { Path = path, Hash = "h-" + path, Type = EntryType.Generic, Size = 1 }).ToList());
    }

    private async Task<(HttpStatusCode Status, string Body)> SearchAsync(params (string Key, string Value)[] fields)
    {
        var (response, body, _) = await SendAsync(Request(HttpMethod.Post, Search, Caller.Owner, Form(fields)));
        return (response.StatusCode, body);
    }

    private static string[] PathsOf(string body) => JsonDocument.Parse(body).RootElement.EnumerateArray()
        .Select(e => e.GetProperty("path").GetString()!).ToArray();

    [Test]
    public async Task WithoutALimit_EveryMatchIsReturned()
    {
        var (status, body) = await SearchAsync(("query", "*"));

        status.ShouldBe(HttpStatusCode.OK, body);
        PathsOf(body).ShouldBe(Paths);
    }

    [Test]
    public async Task ALimitOfOne_ReturnsTheFirstMatch()
    {
        var (status, body) = await SearchAsync(("query", "*"), ("limit", "1"));

        status.ShouldBe(HttpStatusCode.OK, body);
        PathsOf(body).ShouldBe(["a.txt"]);
    }

    [Test]
    public async Task TheLimit_CountsTheMatchesOfTheName_NotTheEntriesOfTheFolder()
    {
        var (status, body) = await SearchAsync(("query", "*.jpg"), ("limit", "2"));

        status.ShouldBe(HttpStatusCode.OK, body);
        PathsOf(body).ShouldBe(["b.jpg", "c.jpg"]);
    }

    [Test]
    public async Task ALimitOverTheMatches_ReturnsAllOfThem()
    {
        var (status, body) = await SearchAsync(("query", "*.jpg"), ("limit", "100"));

        status.ShouldBe(HttpStatusCode.OK, body);
        PathsOf(body).ShouldBe(["b.jpg", "c.jpg", "d.jpg"]);
    }

    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("abc")]
    [TestCase("1.5")]
    [TestCase("99999999999")]
    public async Task ALimitThatIsNotAPositiveWholeNumber_Is400(string limit)
    {
        var (response, body, problem) = await SendAsync(Request(HttpMethod.Post, Search, Caller.Owner,
            Form(("query", "*"), ("limit", limit))));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, body);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(ProblemJson);
        problem.ShouldNotBeNull();
    }

    [Test]
    public async Task AnEmptyLimit_IsAbsent()
    {
        var (status, body) = await SearchAsync(("query", "*"), ("limit", ""));

        status.ShouldBe(HttpStatusCode.OK, body);
        PathsOf(body).Length.ShouldBe(Paths.Length);
    }
}
