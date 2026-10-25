#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Test.Support;
using Shouldly;
using Registry.Test.Common;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Route and authorization smoke tests for every controller, through HTTP.
/// <list type="bullet">
/// <item>The endpoint list comes from the app's own <c>EndpointDataSource</c>
/// (<see cref="EndpointAuthCatalog"/>), so a new controller or action is covered without editing
/// this file, and the checked-in snapshot <c>Snapshots/endpoint-auth.txt</c> ("METHOD /template
/// -> declared auth [gate] @ handler") fails on any added, removed or re-authorized endpoint.
/// Update it on purpose: <c>REGISTRY_UPDATE_SNAPSHOTS=1 dotnet test --filter
/// "FullyQualifiedName~ControllerSmokeTests.EndpointAuthorization_MatchesSnapshot"</c>.</item>
/// <item>Per controller, every endpoint answers its authorization contract: an
/// <see cref="EndpointGate.Authenticated"/> or <see cref="EndpointGate.Admin"/> gate answers 401 to
/// an anonymous request (not 404 or 500), an admin gate answers 403 to a regular user. Endpoints
/// without an HTTP gate (<see cref="EndpointGate.Open"/>: the manager checks access) are called
/// on a private organization/dataset by an anonymous request and by a user who is not a member:
/// the answer must be a 4xx (no data, no 5xx). Model validation runs before the manager's access
/// check, so a 400/415 for the missing body is accepted there. Endpoints outside an organization
/// must not answer 5xx. Gated actions never run (the gate rejects first), so the smoke has no
/// side effects.</item>
/// <item>Known defects are listed in <see cref="Findings"/> with the status they answer today;
/// they appear in the snapshot as <c>// FINDING</c> and the smoke asserts that exact status, so
/// fixing one fails here until its entry is removed.</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ControllerSmokeTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class ControllerSmokeTests : IDisposable
{
    private const string SnapshotName = "endpoint-auth.txt";
    private const string EndpointRoutingGroup = "(endpoint routing)";
    private const string AnyMethod = "ANY";

    private const string OwnerUser = "controllersmoke-owner";
    private const string RegularUser = "controllersmoke-user";
    private const string PrivateOrg = "controllersmoke-private-org";
    private const string PrivateDs = "controllersmoke-private-ds";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private static readonly string[] SnapshotHeader =
    [
        "# Endpoint authorization snapshot (ControllerSmokeTests).",
        "# METHOD /template -> declared authorization [gate] @ handler",
        "#   declared: [AllowAnonymous], [Authorize(...)], inline policies (RequireAuthorization),",
        "#             MVC access filters (BasicAuthFilter, OgcAuthorizationFilter); none = no HTTP gate",
        "#   gate: who passes the HTTP gate: open (manager checks), anonymous, authenticated, admin",
        "# Regenerate on purpose: REGISTRY_UPDATE_SNAPSHOTS=1 dotnet test Registry.Web.UnitTest --filter",
        "#   \"FullyQualifiedName~ControllerSmokeTests.EndpointAuthorization_MatchesSnapshot\""
    ];

    /// <summary>
    /// Defects found by the smoke, not fixed here because the intended behavior is not
    /// an authorization attribute (see each note). The smoke asserts the status they answer today.
    /// </summary>
    private static readonly KnownFinding[] Findings =
    [
        // Fixed findings of the first smoke run: GET import/sources checks write access,
        // member management disabled is a 403 after the access check, a STAC path that is not Base64 is
        // a 400, and the Hangfire dashboard is registered once (an unknown page is Hangfire's 404)
    ];

    private RegistryWebFactory _factory = null!;
    private HttpClient _client = null!;
    private IReadOnlyList<EndpointAuthEntry> _catalog = null!;

    private enum Caller
    {
        Anonymous,
        User
    }

    private sealed record KnownFinding(string Key, int Status, string Note, params Caller[] Callers);

    /// <summary>What a probe must answer: a description for the failure message and the check.</summary>
    private sealed record Expectation(string Description, Func<int, bool> Accepts)
    {
        public static readonly Expectation Challenged = new("401", s => s == 401);
        public static readonly Expectation Forbidden = new("403", s => s == 403);
        public static readonly Expectation ClientError = new("4xx", s => s is >= 400 and < 500);
        public static readonly Expectation NoServerError = new("< 500", s => s is > 0 and < 500);

        public static Expectation Exactly(int status, string note) =>
            new($"{status} (known finding: {note})", s => s == status);
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _factory = new RegistryWebFactory();
        // A dataset without visibility metadata (native ddb throws on a missing key): it is private
        _factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _catalog = await EndpointAuthCatalog.ReadAsync(_factory.Services);

        var owner = await _factory.CreateUserAsync(OwnerUser);
        await _factory.CreateUserAsync(RegularUser);

        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = new Organization
        {
            Slug = PrivateOrg, Name = PrivateOrg, OwnerId = owner.Id, IsPublic = false, CreationDate = DateTime.UtcNow
        };
        context.Organizations.Add(org);
        context.Datasets.Add(new Dataset
        {
            Slug = PrivateDs, Organization = org, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    /// <summary>NUnit disposes the fixture after its last test.</summary>
    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>One smoke group per controller (by reflection) plus the non-MVC endpoints.</summary>
    public static IEnumerable<string> SmokeGroups() =>
        EndpointAuthCatalog.ControllerNames().Append(EndpointRoutingGroup);

    [Test]
    public void EndpointAuthorization_MatchesSnapshot()
    {
        var lines = _catalog.Select(e => e.SnapshotLine + FindingNotes(e.Key)).ToList();

        var failure = SnapshotFile.Verify(SnapshotName, SnapshotHeader, lines);

        failure.ShouldBeNull(failure);
    }

    [Test]
    public void Catalog_CoversEveryController()
    {
        var routed = _catalog.Select(e => e.Controller).OfType<string>().Distinct().Order(StringComparer.Ordinal);

        routed.ShouldBe(EndpointAuthCatalog.ControllerNames(), ignoreOrder: false, customMessage:
            "every controller must map at least one endpoint and every endpoint must belong to a known controller");
    }

    [Test]
    public void Catalog_ClassifiesTheGatesOfKnownEndpoints()
    {
        // Guards the classifier itself: a broken one would produce a self-consistent snapshot
        Gate("GET /sys/config").ShouldBe(EndpointGate.Admin);
        Gate("ANY /health").ShouldBe(EndpointGate.Admin);
        Gate("POST /users/authenticate/refresh").ShouldBe(EndpointGate.Authenticated);
        Gate("GET /sys/features").ShouldBe(EndpointGate.Anonymous);
        Gate("GET /orgs/{orgSlug}/ds").ShouldBe(EndpointGate.Open);
        _catalog.Single(e => e.Key == "GET /orgs/{orgSlug}/ds/{dsSlug}/wms").Declared
            .ShouldBe("Filter(BasicAuthFilter) + Filter(OgcAuthorizationFilter)");
    }

    [Test]
    public void KnownFindings_ReferExistingEndpoints()
    {
        var keys = _catalog.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);

        Findings.Where(f => !keys.Contains(f.Key)).Select(f => f.Key).ShouldBeEmpty(
            "remove the findings of endpoints that no longer exist");
    }

    [TestCaseSource(nameof(SmokeGroups))]
    public async Task Endpoints_AnswerTheirAuthorizationContract(string group)
    {
        var entries = _catalog.Where(e => (e.Controller ?? EndpointRoutingGroup) == group).ToList();
        entries.ShouldNotBeEmpty($"no endpoint is routed to {group}");

        var failures = new List<string>();
        foreach (var entry in entries)
        {
            var url = EndpointAuthCatalog.RenderPath(entry.Endpoint.RoutePattern, RouteValue);
            var method = entry.Method == AnyMethod ? HttpMethod.Get.Method : entry.Method;

            foreach (var (caller, expectation) in Probes(entry))
            {
                var (status, detail) = await SendAsync(method, url, caller);
                if (!expectation.Accepts(status))
                    failures.Add($"{entry.Key} ({entry.Handler}) as {caller}: {method} {url} answered " +
                                 $"{status}, expected {expectation.Description}. {detail}");
            }
        }

        failures.ShouldBeEmpty(string.Join("\n", failures));
    }

    /// <summary>The probes for one endpoint: the contract of its gate, overridden by known findings.</summary>
    private static Dictionary<Caller, Expectation> Probes(EndpointAuthEntry entry)
    {
        var probes = new Dictionary<Caller, Expectation>();
        var orgScoped = entry.Endpoint.RoutePattern.Parameters.Any(p => p.Name == "orgSlug");

        switch (entry.Gate)
        {
            case EndpointGate.Authenticated:
                probes[Caller.Anonymous] = Expectation.Challenged;
                break;
            case EndpointGate.Admin:
            case EndpointGate.Nobody:
                probes[Caller.Anonymous] = Expectation.Challenged;
                probes[Caller.User] = Expectation.Forbidden;
                break;
            case EndpointGate.Open:
            case EndpointGate.Anonymous:
            default:
                var expectation = orgScoped ? Expectation.ClientError : Expectation.NoServerError;
                probes[Caller.Anonymous] = expectation;
                probes[Caller.User] = expectation;
                break;
        }

        var findings = Findings.Where(f => f.Key == entry.Key)
            .SelectMany(f => f.Callers.Select(caller => (Caller: caller, Finding: f)));
        foreach (var (caller, finding) in findings)
            probes[caller] = Expectation.Exactly(finding.Status, finding.Note);

        return probes;
    }

    private static string FindingNotes(string key) =>
        string.Concat(Findings.Where(f => f.Key == key)
            .Select(f => $"  // FINDING ({string.Join(", ", f.Callers)} -> {f.Status}): {f.Note}"));

    /// <summary>
    /// Route values that reach the seeded private organization/dataset (and an existing user);
    /// others are syntactically valid placeholders (except <c>pathBase64</c>, see the findings).
    /// </summary>
    private static string RouteValue(RoutePatternParameterPart parameter) => parameter.Name switch
    {
        "orgSlug" => PrivateOrg,
        "dsSlug" => PrivateDs,
        "userName" => OwnerUser,
        "ext" => "png",
        _ when parameter.ParameterPolicies.Any(p => p.Content == "int") => "0",
        _ => "x"
    };

    private async Task<(int Status, string Detail)> SendAsync(string method, string url, Caller caller)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (caller == Caller.User)
            request.AsUser(RegularUser);

        using var timeout = new CancellationTokenSource(RequestTimeout);
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            var status = (int)response.StatusCode;
            var detail = status >= 500 ? Truncate(await response.Content.ReadAsStringAsync(timeout.Token)) : string.Empty;
            return (status, detail);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return (0, $"no answer within {RequestTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }
        catch (InvalidOperationException ex)
        {
            // TestServer rethrows an exception that escaped the pipeline: Kestrel would answer 500
            return (500, Truncate(ex.Message));
        }
    }

    private static string Truncate(string text)
    {
        const int maxLength = 300;
        var flat = new StringBuilder(text).Replace('\n', ' ').Replace('\r', ' ').ToString();
        return flat.Length <= maxLength ? flat : flat[..maxLength] + "...";
    }

    private EndpointGate Gate(string key) => _catalog.Single(e => e.Key == key).Gate;
}
