using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.Objects;
using Registry.Web.Test.Support;
using Serilog.Core;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The photo coverage endpoint through HTTP on <see cref="RegistryWebFactory"/>, with libddb replaced by a mock: what a
/// request must look like before libddb is asked (the shape of the geometry, the positions it has, the limit, the size
/// of the body), what libddb's refusal becomes (a 400, never a 500), who may ask (the readers of the dataset), and the
/// log of a slow request.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~CoverageHttpTests"
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class CoverageHttpTests : IDisposable
{
    private const string Owner = "cov-owner";
    private const string Outsider = "cov-outsider";
    private const string Org = "cov-org";
    private const string PrivateDs = "private-ds";
    private const string PublicDs = "public-ds";

    private const string Point = """{"type":"Point","coordinates":[9.0005,45.0003]}""";
    private const string Line = """{"type":"LineString","coordinates":[[9.0,45.0],[9.001,45.001]]}""";
    private const string Polygon = """{"type":"Polygon","coordinates":[[[9.0,45.0],[9.001,45.0],[9.001,45.001],[9.0,45.0]]]}""";

    private readonly LogCapture _logs = new();
    private readonly ConcurrentDictionary<string, byte> _publicRefs = new();
    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory(
            new Dictionary<string, string> { ["Serilog:LevelSwitches:$controlSwitch"] = "Information" },
            services => services.AddSingleton<ILogEventSink>(_logs));
        // A dataset is private unless its reference is one of the public ones (native ddb throws on a missing key)
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), "visibility", It.IsAny<string?>()))
            .Returns((string path, string _, string? _) => _publicRefs.Keys.Any(path.Contains)
                ? $$"""{"data":{{(int)Visibility.Public}},"id":"visibility","mtime":0}"""
                : throw new DdbException("Key not found"));
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var owner = await _factory.CreateUserAsync(Owner);
        await _factory.CreateUserAsync(Outsider);
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, Org, ownerId: owner.Id, isPublic: false);
        Seed.Dataset(context, org, PrivateDs);
        _publicRefs[Seed.Dataset(context, org, PublicDs).InternalRef.ToString()] = 0;
        await context.SaveChangesAsync();
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [SetUp]
    public void SetUp()
    {
        _logs.Clear();
        _factory.DdbWrapper.Invocations.Clear();
        // No image sees the place, unless a test sets up its own answer (the last setup of a call wins)
        _factory.DdbWrapper.Setup(d => d.FindCoverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Returns([]);
    }

    private static string Url(string ds = PrivateDs) => $"/orgs/{Org}/ds/{ds}/coverage";

    private async Task<(int Status, string Body)> PostAsync(string body, string? user = Owner, string ds = PrivateDs)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(ds))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (user != null)
            request.AsUser(user);
        using var response = await _client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Request(string geometry, string? limit = null) =>
        limit == null ? $$"""{"geometry":{{geometry}}}""" : $$"""{"geometry":{{geometry}},"limit":{{limit}}}""";

    private void NeverAsked() =>
        _factory.DdbWrapper.Verify(d => d.FindCoverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()),
            Times.Never);

    #region Answer

    [Test]
    public async Task TheImagesOfLibddb_AreTheAnswer_InItsOrder()
    {
        string? folder = null;
        string? sent = null;
        int? sentLimit = -1;
        _factory.DdbWrapper.Setup(d => d.FindCoverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Callback<string, string, int?>((f, g, l) => (folder, sent, sentLimit) = (f, g, l))
            .Returns([
                new CoverageResult { Path = "images/DJI_0412.JPG", Score = 0.08, Overlap = 1.0, Pixel = [2731, 1904] },
                new CoverageResult { Path = "images/DJI_0413.JPG", Score = 0.21, Overlap = 0.5 }
            ]);

        var (status, body) = await PostAsync(Request(Point));

        status.ShouldBe(200, body);
        var answer = JsonNode.Parse(body)!.AsArray();
        answer.Count.ShouldBe(2);
        answer[0]!["path"]!.GetValue<string>().ShouldBe("images/DJI_0412.JPG");
        answer[0]!["score"]!.GetValue<double>().ShouldBe(0.08);
        answer[0]!["overlap"]!.GetValue<double>().ShouldBe(1.0);
        answer[0]!["pixel"]!.AsArray().Select(n => n!.GetValue<double>()).ShouldBe([2731, 1904]);
        answer[1]!["path"]!.GetValue<string>().ShouldBe("images/DJI_0413.JPG");
        answer[1]!["pixel"].ShouldBeNull();
        // The geometry goes to libddb as it came, the limit as not set: libddb's default applies
        JsonNode.Parse(sent!)!.ToJsonString().ShouldBe(JsonNode.Parse(Point)!.ToJsonString());
        sentLimit.ShouldBeNull();
        folder.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task NoImage_IsAnEmptyArray()
    {
        var (status, body) = await PostAsync(Request(Point));

        status.ShouldBe(200, body);
        JsonNode.Parse(body)!.AsArray().Count.ShouldBe(0);
    }

    [TestCase(Point)]
    [TestCase(Line)]
    [TestCase(Polygon)]
    public async Task APointALineAndAPolygon_AreAsked(string geometry)
    {
        (await PostAsync(Request(geometry))).Status.ShouldBe(200);

        _factory.DdbWrapper.Verify(d => d.FindCoverage(It.IsAny<string>(), It.IsAny<string>(), null), Times.Once);
    }

    [TestCase("1")]
    [TestCase("20")]
    [TestCase("100")]
    public async Task TheLimit_ReachesLibddb(string limit)
    {
        (await PostAsync(Request(Point, limit))).Status.ShouldBe(200);

        _factory.DdbWrapper.Verify(d => d.FindCoverage(It.IsAny<string>(), It.IsAny<string>(), int.Parse(limit, CultureInfo.InvariantCulture)),
            Times.Once);
    }

    [Test]
    public async Task APositionWithAHeight_IsAnAcceptedPosition()
    {
        var (status, body) = await PostAsync(Request("""{"type":"Point","coordinates":[9.0,45.0,120.5]}"""));

        status.ShouldBe(200, body);
    }

    #endregion

    #region Refused before libddb

    [TestCase("""{"type":"MultiPolygon","coordinates":[[[[9,45],[9.1,45],[9.1,45.1],[9,45]]]]}""", "MultiPolygon")]
    [TestCase("""{"type":"MultiPoint","coordinates":[[9,45]]}""", "MultiPoint")]
    [TestCase("""{"type":"MultiLineString","coordinates":[[[9,45],[9.1,45]]]}""", "MultiLineString")]
    [TestCase("""{"type":"Feature","geometry":{"type":"Point","coordinates":[9,45]},"properties":{}}""", "Feature")]
    [TestCase("""{"type":"FeatureCollection","features":[]}""", "FeatureCollection")]
    [TestCase("""{"type":"GeometryCollection","geometries":[]}""", "GeometryCollection")]
    [TestCase("""{"type":"point","coordinates":[9,45]}""", "'point'")]
    public async Task AGeometryOfAnotherType_Is400(string geometry, string named)
    {
        var (status, body) = await PostAsync(Request(geometry));

        status.ShouldBe(400, body);
        JsonNode.Parse(body)!["errors"]!["geometry"]![0]!.GetValue<string>().ShouldContain(named);
        NeverAsked();
    }

    [TestCase("""{"coordinates":[9,45]}""")]
    [TestCase("""{"type":"Point"}""")]
    [TestCase("""{"type":7,"coordinates":[9,45]}""")]
    [TestCase("""{"type":"Point","coordinates":9}""")]
    [TestCase("""{"type":"Point","coordinates":[9]}""")]
    [TestCase("""{"type":"Point","coordinates":["9","45"]}""")]
    [TestCase("""{"type":"Point","coordinates":[[9,45]]}""")]
    [TestCase("""{"type":"LineString","coordinates":[[9,45]]}""")]
    [TestCase("""{"type":"LineString","coordinates":[9,45]}""")]
    [TestCase("""{"type":"LineString","coordinates":[[9,45],[9.1]]}""")]
    [TestCase("""{"type":"Polygon","coordinates":[]}""")]
    [TestCase("""{"type":"Polygon","coordinates":[[[9,45],[9.1,45],[9,45]]]}""")]
    [TestCase("""{"type":"Polygon","coordinates":[[9,45],[9.1,45],[9.1,45.1],[9,45]]}""")]
    [TestCase("""[9,45]""")]
    [TestCase("\"POINT (9 45)\"")]
    [TestCase("""null""")]
    [TestCase("""{}""")]
    public async Task AGeometryThatIsNotWellFormed_Is400(string geometry)
    {
        var (status, body) = await PostAsync(Request(geometry));

        status.ShouldBe(400, body);
        JsonNode.Parse(body)!["errors"]!["geometry"].ShouldNotBeNull();
        NeverAsked();
    }

    [TestCase("""{}""")]
    [TestCase("""{"limit":5}""")]
    public async Task ARequestWithoutAGeometry_Is400(string body)
    {
        var (status, answer) = await PostAsync(body);

        status.ShouldBe(400, answer);
        JsonNode.Parse(answer)!["errors"]!["geometry"].ShouldNotBeNull();
        NeverAsked();
    }

    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("""{"geometry":""")]
    public async Task ABodyThatIsNotARequest_Is400(string body)
    {
        var (status, _) = await PostAsync(body);

        status.ShouldBe(400);
        NeverAsked();
    }

    [TestCase(CoverageGeometry.MaxVertices, 200)]
    [TestCase(CoverageGeometry.MaxVertices + 1, 400)]
    public async Task TheNumberOfPositions_IsLimited(int vertices, int expected)
    {
        var (status, body) = await PostAsync(Request(LineOf(vertices)));

        status.ShouldBe(expected, body);
        if (expected == 400)
        {
            JsonNode.Parse(body)!["errors"]!["geometry"]![0]!.GetValue<string>()
                .ShouldContain($"at most {CoverageGeometry.MaxVertices} positions");
            NeverAsked();
        }
    }

    [Test]
    public async Task ThePositionsOfAllTheRingsOfAPolygon_AreCounted()
    {
        // Two rings of 5001 positions
        var ring = "[" + string.Join(",", Enumerable.Repeat("[1,2]", 5001)) + "]";

        var (status, body) = await PostAsync(Request($$"""{"type":"Polygon","coordinates":[{{ring}},{{ring}}]}"""));

        status.ShouldBe(400, body);
        NeverAsked();
    }

    [TestCase("0")]
    [TestCase("101")]
    [TestCase("-1")]
    [TestCase("1000000")]
    public async Task ALimitOutOfRange_Is400(string limit)
    {
        var (status, body) = await PostAsync(Request(Point, limit));

        status.ShouldBe(400, body);
        JsonNode.Parse(body)!["errors"]!["limit"]![0]!.GetValue<string>().ShouldBe("limit must be between 1 and 100");
        NeverAsked();
    }

    [TestCase("1.5")]
    [TestCase("true")]
    [TestCase("[]")]
    public async Task ALimitThatIsNotAWholeNumber_Is400(string limit)
    {
        var (status, _) = await PostAsync(Request(Point, limit));

        status.ShouldBe(400);
        NeverAsked();
    }

    [Test]
    public async Task ABodyOverTheLimit_Is413_BeforeAnythingIsRead()
    {
        var body = Request(Point) + new string(' ', 64 * 1024);

        var (status, _) = await PostAsync(body);

        status.ShouldBe(413);
        NeverAsked();
    }

    [Test]
    public async Task ABodyOfExactlyTheLimit_IsRead()
    {
        var json = Request(Point);
        var body = json + new string(' ', 64 * 1024 - json.Length);

        var (status, answer) = await PostAsync(body);

        status.ShouldBe(200, answer);
    }

    private static string LineOf(int vertices) =>
        $$"""{"type":"LineString","coordinates":[{{string.Join(",", Enumerable.Repeat("[1,2]", vertices))}}]}""";

    #endregion

    #region What libddb refuses

    [Test]
    public async Task APositionLibddbRefuses_Is400_WithItsMessage()
    {
        _factory.DdbWrapper.Setup(d => d.FindCoverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Throws(new DdbInvalidArgumentsException("Latitude out of range: 91"));

        var (status, body) = await PostAsync(Request("""{"type":"Point","coordinates":[9,91]}"""));

        status.ShouldBe(400, body);
        var problem = JsonNode.Parse(body)!;
        problem["detail"]!.GetValue<string>().ShouldContain("Latitude out of range: 91");
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public async Task AFailureOfLibddb_IsNotAnInvalidRequest()
    {
        _factory.DdbWrapper.Setup(d => d.FindCoverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Throws(new DdbException("database is corrupt"));

        var (status, _) = await PostAsync(Request(Point));

        status.ShouldBe(500);
    }

    #endregion

    #region Who can ask

    [Test]
    public async Task TheReadersOfTheDataset_CanAsk()
    {
        (await PostAsync(Request(Point), Owner)).Status.ShouldBe(200);
        (await PostAsync(Request(Point), Outsider, PublicDs)).Status.ShouldBe(200);
        (await PostAsync(Request(Point), null, PublicDs)).Status.ShouldBe(200);
    }

    [Test]
    public async Task APrivateDataset_IsNotAnsweredToTheOthers()
    {
        (await PostAsync(Request(Point), Outsider)).Status.ShouldBe(403);
        (await PostAsync(Request(Point), null)).Status.ShouldBe(401);
        NeverAsked();
    }

    [Test]
    public async Task ADatasetThatIsNotThere_Is404()
    {
        (await PostAsync(Request(Point), Owner, "no-such-ds")).Status.ShouldBe(404);
        NeverAsked();
    }

    [Test]
    public async Task TheAccess_IsCheckedBeforeTheBody()
    {
        // The refusal of a dataset does not tell whether the geometry was a good one
        (await PostAsync("""{"geometry":7}""", Outsider)).Status.ShouldBe(403);
        (await PostAsync("""{"geometry":7}""", null)).Status.ShouldBe(401);
        (await PostAsync("""{"geometry":7}""", Owner, "no-such-ds")).Status.ShouldBe(404);
    }

    #endregion

    #region The log

    [Test]
    public async Task ARequestOverASecond_IsLogged_WithTheDatasetTheVerticesAndTheDuration()
    {
        _factory.DdbWrapper.Setup(d => d.FindCoverage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int?>()))
            .Returns(() =>
            {
                Thread.Sleep(CoverageService.SlowRequest + TimeSpan.FromMilliseconds(150));
                return [];
            });

        (await PostAsync(Request(LineOf(3)))).Status.ShouldBe(200);

        var slow = _logs.Records.Where(r => r.Category == typeof(CoverageService).FullName).ToList();
        slow.Count.ShouldBe(1);
        slow[0].Level.ShouldBe(LogLevel.Warning);
        slow[0].Message.ShouldContain(Org);
        slow[0].Message.ShouldContain(PrivateDs);
        slow[0].Message.ShouldContain("3 positions");
        var milliseconds = int.Parse(slow[0].Message.Split(" took ")[1].Split(" ms")[0], CultureInfo.InvariantCulture);
        milliseconds.ShouldBeGreaterThan(1000);
    }

    [Test]
    public async Task AFastRequest_IsNotLogged()
    {
        (await PostAsync(Request(Point))).Status.ShouldBe(200);

        _logs.Records.Where(r => r.Category == typeof(CoverageService).FullName).ShouldBeEmpty();
    }

    #endregion
}
