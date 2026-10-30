using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Adapters.DroneDB;
using Registry.Adapters.Testing;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Services.Hub;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// Golden JSON contract tests. Every JSON endpoint the Hub calls
/// (<c>ClientApp/webapp/js/libs/ddb/*.js</c>) is requested through <see cref="RegistryWebFactory"/>
/// and its status, Content-Type, contract headers and scrubbed body are compared with
/// <c>Snapshots/golden/&lt;case&gt;.json</c> (<see cref="GoldenResponse"/>, <see cref="JsonScrubber"/>;
/// <c>Snapshots/golden/README.md</c> lists the cases and the scrubbed fields). Representative
/// 400/401/403/404 answers are recorded too: the error format is ProblemDetails.
/// These goldens are the safety net of the wire format
/// (ProblemDetails errors, System.Text.Json): any difference must be on purpose and listed in the goldens README. Update on
/// purpose: <c>REGISTRY_UPDATE_SNAPSHOTS=1 dotnet test Registry.Web.UnitTest --filter
/// "FullyQualifiedName~GoldenJsonTests"</c>, then review the diff.
/// <para>
/// Determinism: read-only cases share one host; a case that changes state
/// (<see cref="GoldenCase.Isolated"/>) gets its own host, so no golden depends on the test order.
/// The status of every case is declared, so a 5xx is never recorded as a golden, even in update
/// mode. libddb is answered by <see cref="FakeDdbWrapper"/> (the DroneDB contract fixtures); the
/// calls no fixture records are set up by <see cref="GoldenDdb"/> with the values libddb returns.
/// </para>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~GoldenJsonTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class GoldenJsonTests : IDisposable
{
    private const string GoldenFolder = "golden";

    private const string OwnerUser = "golden-owner";
    private const string WriterUser = "golden-writer";
    private const string RegularUser = "golden-user";

    private const string Org = "golden-org";
    private const string Ds = "golden-ds";
    private const string WriteOrg = "golden-write";
    private const string MissingDs = "golden-missing";

    private const string DsApi = "/orgs/" + Org + "/ds/" + Ds;

    /// <summary>Response headers that are part of the Hub contract (paging).</summary>
    private static readonly string[] ContractHeaders = ["X-Total-Count"];

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private GoldenHost _shared = null!;

    /// <summary>Who sends a request: the test identity header of <see cref="RegistryWebFactory"/>.</summary>
    public enum Caller
    {
        Anonymous,
        User,
        Owner,
        Writer,
        Admin
    }

    /// <summary>
    /// One golden: <paramref name="Name"/> is the file name (without <c>.json</c>),
    /// <paramref name="Status"/> the status the request must answer before its body is compared.
    /// </summary>
    public sealed record GoldenCase(string Name, Caller Caller, HttpMethod Method, string Url, int Status)
    {
        public IReadOnlyDictionary<string, string>? Form { get; init; }
        public string? Json { get; init; }

        /// <summary>The request changes state: it runs on a host of its own.</summary>
        public bool Isolated { get; init; }

        /// <summary>Runs on the scrubbed body: sorts arrays whose server order is not part of the contract.</summary>
        public Action<JsonNode?>? Normalise { get; init; }

        public override string ToString() => Name;
    }

    [OneTimeSetUp]
    public async Task OneTimeSetUp() => _shared = await GoldenHost.StartAsync();

    /// <summary>NUnit disposes the fixture after its last test.</summary>
    public void Dispose() => _shared?.Dispose();

    public static IEnumerable<GoldenCase> Cases()
    {
        var get = HttpMethod.Get;
        var post = HttpMethod.Post;

        // ---- system (sysApi.js, featuresService.js, adminApi.js)
        yield return new("sys-config", Caller.Admin, get, "/sys/config", 200);
        yield return new("sys-version", Caller.User, get, "/sys/version", 200);
        yield return new("sys-features", Caller.Anonymous, get, "/sys/features", 200);
        yield return new("sys-processing-nodes", Caller.User, get, "/sys/processingNodes", 200);
        yield return new("sys-tasks", Caller.Admin, get, "/sys/tasks", 200);

        // ---- users (authSession.js, adminApi.js, registry.js); the identity store has no defined
        // order, so users are listed in name order
        yield return new("users-authenticate", Caller.Anonymous, post, "/users/authenticate", 200)
        {
            Form = new Dictionary<string, string>
            {
                ["username"] = RegistryWebFactory.AdminUserName, ["password"] = RegistryWebFactory.AdminPassword
            }
        };
        yield return new("users-authenticate-refresh", Caller.Admin, post, "/users/authenticate/refresh", 200);
        yield return new("users-list", Caller.Admin, get, "/users", 200)
        {
            Normalise = body => JsonScrubber.SortArray(body, [], "userName")
        };
        yield return new("users-detailed", Caller.Admin, get, "/users/detailed", 200)
        {
            Normalise = body => JsonScrubber.SortArray(body, [], "userName")
        };
        yield return new("users-roles", Caller.Admin, get, "/users/roles", 200);
        yield return new("users-storage", Caller.Owner, get, "/users/storage", 200);
        yield return new("users-user-storage", Caller.Admin, get, $"/users/{OwnerUser}/storage", 200);
        yield return new("users-user-orgs", Caller.Admin, get, $"/users/{OwnerUser}/orgs", 200);
        yield return new("users-create", Caller.Admin, post, "/users", 200)
        {
            Json = """{"userName":"goldennew","email":"goldennew@example.com","password":"Password1234","roles":[]}""",
            Isolated = true
        };

        // ---- organizations (organizationsApi.js, organization.js); OrganizationsManager.List has no
        // ORDER BY, so organizations are listed in slug order
        yield return new("orgs-list", Caller.Owner, get, "/orgs", 200)
        {
            Normalise = body => JsonScrubber.SortArray(body, [], "slug")
        };
        yield return new("orgs-list-admin", Caller.Admin, get, "/orgs", 200)
        {
            Normalise = body => JsonScrubber.SortArray(body, [], "slug")
        };
        yield return new("orgs-create", Caller.Owner, post, "/orgs", 201)
        {
            Form = new Dictionary<string, string>
            {
                ["slug"] = "golden-new-org", ["name"] = "Golden new organization",
                ["description"] = "Created by the golden tests", ["isPublic"] = "false"
            },
            Isolated = true
        };
        yield return new("orgs-get", Caller.Owner, get, $"/orgs/{Org}/", 200);
        yield return new("orgs-datasets", Caller.Owner, get, $"/orgs/{Org}/ds", 200);
        yield return new("orgs-datasets-paged", Caller.Owner, get,
            $"/orgs/{Org}/ds?includeThumbnailCheck=true&skip=0&take=10&sort=name", 200);
        yield return new("orgs-import-sources", Caller.Owner, get, $"/orgs/{Org}/import/sources", 200);
        yield return new("orgs-datasets-create", Caller.Writer, post, $"/orgs/{WriteOrg}/ds", 201)
        {
            Form = new Dictionary<string, string>
            {
                ["slug"] = "golden-created", ["name"] = "Golden created", ["visibility"] = "1"
            },
            Isolated = true
        };

        // ---- dataset (dataset.js)
        yield return new("ds-get", Caller.Owner, get, DsApi, 200);
        yield return new("ds-list-root", Caller.Owner, post, DsApi + "/list", 200)
        {
            Form = new Dictionary<string, string>()
        };
        yield return new("ds-list-folder", Caller.Owner, post, DsApi + "/list", 200)
        {
            Form = new Dictionary<string, string> { ["path"] = "sub" }
        };
        yield return new("ds-search", Caller.Owner, post, DsApi + "/search", 200)
        {
            Form = new Dictionary<string, string> { ["query"] = "*" }
        };
        yield return new("ds-builds-pending", Caller.Owner, get, DsApi + "/builds/pending", 200);
        // The other dataset answers that carry libddb values (stamp, attributes, meta dump) or lists (batches)
        yield return new("ds-ex", Caller.Owner, get, DsApi + "/ex", 200);
        yield return new("ds-stamp", Caller.Owner, get, DsApi + "/stamp", 200);
        yield return new("ds-batches", Caller.Owner, get, DsApi + "/batches", 200);
        yield return new("ds-chattr", Caller.Owner, post, DsApi + "/chattr", 200)
        {
            Form = new Dictionary<string, string> { ["attrs"] = """{"public":true}""" },
            Isolated = true
        };
        yield return new("meta-dump", Caller.Owner, post, DsApi + "/meta/dump", 200)
        {
            Form = new Dictionary<string, string>()
        };
        yield return new("share-init", Caller.Owner, post, "/share/init", 200)
        {
            Form = new Dictionary<string, string> { ["tag"] = $"{Org}/{Ds}" },
            Isolated = true
        };
        yield return new("users-meta", Caller.Owner, get, "/users/meta", 200);
        yield return new("users-user-meta", Caller.Admin, get, $"/users/{OwnerUser}/meta", 200);
        yield return new("orgs-public", Caller.Anonymous, get, "/orgs/public", 200);

        // ---- meta (dataset.js meta/set, meta/unset; MetaController reads). The fixtures are
        // stateless: set/unset do not change what a later read returns
        yield return new("meta-list", Caller.Owner, get, DsApi + "/meta/list", 200);
        yield return new("meta-list-entry", Caller.Owner, get, DsApi + "/meta/list?path=sub/img.jpg", 200);
        yield return new("meta-get", Caller.Owner, get, DsApi + "/meta/get/annotation?path=sub/img.jpg", 200);
        yield return new("meta-get-list", Caller.Owner, get, DsApi + "/meta/get/tags", 200);
        yield return new("meta-set", Caller.Owner, post, DsApi + "/meta/set", 200)
        {
            Form = new Dictionary<string, string>
            {
                ["key"] = "annotation", ["data"] = """{"note":"tree","level":2}""", ["path"] = "sub/img.jpg"
            }
        };
        yield return new("meta-unset", Caller.Owner, post, DsApi + "/meta/unset", 200)
        {
            Form = new Dictionary<string, string> { ["key"] = "name" }
        };

        // ---- tasks (dataset.js)
        yield return new("tasks-tools", Caller.Owner, get, DsApi + "/tasks/tools", 200);
        yield return new("tasks-list", Caller.Owner, get, DsApi + "/tasks?skip=0&take=50", 200);

        // ---- STAC (StacController; the Hub links the dataset collection)
        yield return new("stac-collection", Caller.Owner, get, DsApi + "/stac", 200);
        yield return new("stac-item", Caller.Owner, get, DsApi + "/stac/" + Base64("ortho.tif"), 200);
        yield return new("stac-item-image", Caller.Owner, get, DsApi + "/stac/" + Base64("sub/img.jpg"), 200);

        // ---- STAC API 1.0.0 (StacController): Registry builds every STAC id, link
        // and href from libddb's data-only documents, so a change of them shows here
        yield return new("stac-api-landing", Caller.Owner, get, "/stac", 200);
        yield return new("stac-api-collections", Caller.Owner, get, "/stac/collections", 200);
        yield return new("stac-api-collection", Caller.Owner, get, $"/stac/collections/{Org}/{Ds}", 200);
        yield return new("stac-api-items", Caller.Owner, get, $"/stac/collections/{Org}/{Ds}/items", 200);
        yield return new("stac-api-item", Caller.Owner, get,
            $"/stac/collections/{Org}/{Ds}/items/{Base64Url("ortho.tif")}", 200);
        yield return new("stac-api-search", Caller.Owner, get, $"/stac/search?collections={Org}/{Ds}", 200);

        // ---- OGC API features (dataset.js ogcapi/collections/{id}/items)
        yield return new("ogcapi-landing", Caller.Owner, get, DsApi + "/ogcapi?f=json", 200);
        yield return new("ogcapi-conformance", Caller.Owner, get, DsApi + "/ogcapi/conformance?f=json", 200);
        yield return new("ogcapi-collections", Caller.Owner, get, DsApi + "/ogcapi/collections?f=json", 200);

        // ---- errors: RFC 9457 ProblemDetails; 401 only for anonymous callers
        yield return new("error-400-invalid-slug", Caller.Writer, post, $"/orgs/{WriteOrg}/ds", 400)
        {
            Form = new Dictionary<string, string> { ["slug"] = "Not A Slug!" }
        };
        yield return new("error-400-model-validation", Caller.Owner, post, DsApi + "/meta/remove", 400)
        {
            Form = new Dictionary<string, string>()
        };
        yield return new("error-401-anonymous-gate", Caller.Anonymous, get, "/users/storage", 401);
        yield return new("error-401-anonymous-private-org", Caller.Anonymous, get, $"/orgs/{Org}/ds", 401);
        // an authenticated user without access gets 403
        yield return new("error-403-non-member", Caller.User, get, DsApi, 403);
        yield return new("error-403-non-admin", Caller.User, get, "/sys/config", 403);
        yield return new("error-404-missing-dataset", Caller.Owner, get, $"/orgs/{Org}/ds/{MissingDs}", 404);
        yield return new("error-404-missing-organization", Caller.Owner, get, "/orgs/golden-nowhere/", 404);
    }

    [TestCaseSource(nameof(Cases))]
    public async Task Response_MatchesGolden(GoldenCase golden)
    {
        var isolated = golden.Isolated ? await GoldenHost.StartAsync() : null;
        try
        {
            var rendered = await (isolated ?? _shared).RenderAsync(golden);

            var failure = SnapshotFile.VerifyDocument($"{GoldenFolder}/{golden.Name}.json", rendered);
            failure.ShouldBeNull(failure);
        }
        finally
        {
            isolated?.Dispose();
        }
    }

    [Test]
    public void Cases_HaveUniqueNames()
    {
        Cases().GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key).ShouldBeEmpty();
    }

    [Test]
    public void GoldenFolder_HasNoStaleFiles()
    {
        // A golden whose case was removed would never be compared again: delete it with the case
        var folder = SnapshotFile.PathOf(GoldenFolder);
        Directory.Exists(folder).ShouldBeTrue(folder);

        var names = Cases().Select(c => c.Name + ".json").ToHashSet(StringComparer.Ordinal);
        Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileName).OfType<string>()
            .Where(f => !names.Contains(f)).ShouldBeEmpty();
    }

    [Test]
    public void Scrubber_KeepsTypesAndOffsetForms()
    {
        // Guards the scrubber itself: a broken one would produce self-consistent goldens
        var scrubber = new JsonScrubber(
            properties: new Dictionary<string, JsonNode> { ["token"] = JsonScrubber.TokenPlaceholder, ["epoch"] = 0 },
            substrings: new Dictionary<string, string> { ["/tmp/root"] = JsonScrubber.RootPlaceholder },
            literals: new Dictionary<string, string> { ["9.9.9"] = JsonScrubber.VersionPlaceholder });

        var scrubbed = scrubber.Scrub(JsonNode.Parse("""
            {"Token":"abc","epoch":1761000000,"path":"/tmp/root/a/0a223495-84a0-4c15-b425-c7ef88110e75.zip",
             "v":"9.9.9","jwt":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig","n":1.0,
             "dates":["2026-10-21T07:31:17.1234567","2026-10-21T07:31:17Z","2026-10-21T07:31:17.5+02:00"]}
            """))!.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        scrubbed.ShouldBe("""
            {"Token":"<token>","epoch":0,"path":"<root>/a/<guid>.zip","v":"<version>","jwt":"<jwt>","n":1.0,"dates":["<datetime>","<datetime:Z>","<datetime:offset>"]}
            """);
    }

    [Test]
    public void OrderedDiff_ShowsChangedLinesInPlace()
    {
        var diff = SnapshotFile.OrderedDiff(["{", "  \"a\": 1,", "  \"b\": 2", "}"],
            ["{", "  \"A\": 1,", "  \"b\": 2", "}"], context: 1);

        diff.ShouldBe(["@@ line 1", "  {", "-   \"a\": 1,", "+   \"A\": 1,", "    \"b\": 2"]);
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // The STAC API feature id of a path
    private static string Base64Url(string text) => Base64(text).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// A seeded Registry host (<see cref="RegistryWebFactory"/>) with its client and the scrubber of
    /// its volatile values (temp root, Registry/Hub/libddb versions, tokens).
    /// Seed: users <c>golden-owner</c> (owns <c>golden-org</c> with the contract dataset
    /// <c>golden-ds</c>), <c>golden-writer</c> (owns the empty <c>golden-write</c>) and
    /// <c>golden-user</c> (no membership), plus the default admin and the public organization.
    /// </summary>
    private sealed class GoldenHost : IDisposable
    {
        private readonly RegistryWebFactory _factory;
        private readonly GoldenDdb _ddb = new();
        private readonly HttpClient _client;
        private JsonScrubber _scrubber = null!;

        private GoldenHost()
        {
            _factory = new RegistryWebFactory();
            _ddb.Configure(_factory.DdbWrapper);
            _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        }

        public static async Task<GoldenHost> StartAsync()
        {
            var host = new GoldenHost();
            try
            {
                await host.SeedAsync();
                host._scrubber = await host.CreateScrubberAsync();
                return host;
            }
            catch
            {
                host.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
        }

        /// <summary>Sends the case, checks its status and renders its golden form.</summary>
        public async Task<string> RenderAsync(GoldenCase golden)
        {
            using var request = BuildRequest(golden);
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var response = await _client.SendAsync(request, timeout.Token);

            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            status.ShouldBe(golden.Status,
                $"{golden.Method} {golden.Url} as {golden.Caller} answered {status}: {Truncate(body)}");

            var headers = ContractHeaders
                .Where(h => response.Headers.Contains(h))
                .Select(h => new KeyValuePair<string, string>(h, string.Join(", ", response.Headers.GetValues(h))));

            return GoldenResponse.Render($"{golden.Method} {golden.Url}", CallerName(golden.Caller), status,
                response.Content.Headers.ContentType?.ToString(), body, _scrubber, golden.Normalise, headers);
        }

        private async Task SeedAsync()
        {
            var owner = await _factory.CreateUserAsync(OwnerUser);
            var writer = await _factory.CreateUserAsync(WriterUser);
            await _factory.CreateUserAsync(RegularUser);

            using var scope = _factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
            var org = Seed.Organization(context, Org, "Golden organization", ownerId: owner.Id,
                description: "Golden JSON contract tests");
            var dataset = Seed.Dataset(context, org, Ds);
            Seed.Organization(context, WriteOrg, "Golden write organization", ownerId: writer.Id);
            await context.SaveChangesAsync();

            _ddb.AddContractDataset(dataset.InternalRef);
        }

        private async Task<JsonScrubber> CreateScrubberAsync()
        {
            // The Registry version changes with every release: read it back from the app
            using var request = new HttpRequestMessage(HttpMethod.Get, "/sys/version").AsUser(RegularUser);
            using var response = await _client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var registryVersion = (await response.Content.ReadAsStringAsync()).Trim('"');

            var literals = new Dictionary<string, string>
            {
                [registryVersion] = JsonScrubber.VersionPlaceholder,
                [RegistryWebFactory.FakeDdbVersion] = JsonScrubber.VersionPlaceholder
            };
            if (!string.IsNullOrEmpty(HubInfo.CurrentVersion))
                literals[HubInfo.CurrentVersion] = JsonScrubber.VersionPlaceholder;

            return new JsonScrubber(
                properties: new Dictionary<string, JsonNode>
                {
                    ["token"] = JsonScrubber.TokenPlaceholder,
                    // ProblemDetails: the W3C trace id of the request
                    ["traceId"] = JsonScrubber.TraceIdPlaceholder,
                    // Token expiry: epoch seconds (a number), now + TokenExpirationInDays
                    ["expires"] = 0,
                    // HubInfo is process-wide: null or a version depending on what ran before
                    ["hubVersion"] = JsonScrubber.VersionPlaceholder,
                    ["registryVersion"] = JsonScrubber.VersionPlaceholder,
                    ["ddbVersion"] = JsonScrubber.VersionPlaceholder,
                    // Random per host: the hash of the instance id created at its first start
                    ["instanceHash"] = "<instance-hash>"
                },
                substrings: new Dictionary<string, string>
                {
                    [_factory.RootPath.Replace('\\', '/')] = JsonScrubber.RootPlaceholder,
                    [_factory.RootPath] = JsonScrubber.RootPlaceholder,
                    // The factory's folders are Path.Combine'd under the root: '\' on Windows
                    [_factory.RootPath + Path.DirectorySeparatorChar] = JsonScrubber.RootPlaceholder + "/"
                },
                literals: literals);
        }

        private static HttpRequestMessage BuildRequest(GoldenCase golden)
        {
            var request = new HttpRequestMessage(golden.Method, golden.Url);
            if (golden.Form != null)
                request.Content = new FormUrlEncodedContent(golden.Form);
            else if (golden.Json != null)
                request.Content = new StringContent(golden.Json, Encoding.UTF8,
                    new MediaTypeHeaderValue("application/json"));

            return golden.Caller switch
            {
                Caller.Anonymous => request,
                Caller.User => request.AsUser(RegularUser),
                Caller.Owner => request.AsUser(OwnerUser),
                Caller.Writer => request.AsUser(WriterUser),
                Caller.Admin => request.AsUser(RegistryWebFactory.AdminUserName, isAdmin: true),
                _ => throw new ArgumentOutOfRangeException(nameof(golden), golden.Caller, null)
            };
        }

        private static string CallerName(Caller caller) => caller switch
        {
            Caller.Anonymous => "anonymous",
            Caller.User => "user (no membership)",
            Caller.Owner => "organization owner",
            Caller.Writer => "organization owner (write org)",
            Caller.Admin => "admin",
            _ => caller.ToString()
        };

        private static string Truncate(string text) => text.Length <= 500 ? text : text[..500] + "...";
    }

    /// <summary>
    /// The libddb answers the contract fixtures do not record, set up on a host's
    /// <see cref="Mock{FakeDdbWrapper}"/> with the shapes libddb returns:
    /// <list type="bullet">
    /// <item>dataset root meta (<c>DDBMetaGet</c>/<c>DDBMetaSet</c> on the root, path ""): a
    /// per-folder store (seeded <c>name</c> + <c>visibility</c> for the contract dataset), a missing
    /// key throws <see cref="DdbException"/> like libddb, data that is not JSON is stored as a JSON
    /// string (<c>MetaManager::validateData</c>); the keys the fixtures record (<c>annotation</c>,
    /// <c>tags</c>) still come from the fixtures;</item>
    /// <item>dataset root info (<c>DDBInfo</c> of the dataset folder without hash): a
    /// <see cref="EntryType.DroneDB"/> entry whose <c>properties</c> are <c>entries</c> and
    /// <c>meta</c> (<c>Database::getProperties</c>), the entry count and size being those of the
    /// contract dataset (<c>list</c> fixture), or 0 for a dataset created by a test;</item>
    /// <item>entry queries (<c>DDBListEntries</c>/<c>DDBListEntrySummaries</c>) the fixtures do not
    /// record: the whole-dataset fixture (<c>list-entries</c>/<c>list-entry-summaries</c>) filtered
    /// with libddb's rules (<c>dbops.cpp listEntries</c>: folder descendants, depth when not
    /// recursive, types, case-insensitive name glob, ordered by path); an empty list for a dataset
    /// created by a test;</item>
    /// <item><c>DDBGet</c> of an indexed path no <c>get-*</c> fixture records: its entry in the
    /// <c>list</c> fixture (the same <c>Entry</c> JSON).</item>
    /// </list>
    /// </summary>
    private sealed class GoldenDdb
    {
        private const long FixtureMtime = 1700000000;

        private static readonly string[] FixtureMetaKeys = ["annotation", "tags"];

        /// <summary>Plain fixture replay, for the derived answers (not the mock: no recursion).</summary>
        private readonly FakeDdbWrapper _fixtures = new();

        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JsonNode?>> _rootMeta =
            new(StringComparer.Ordinal);

        private readonly ConcurrentDictionary<Guid, byte> _contractDatasets = new();

        /// <summary>The dataset with this internal reference stands for the contract dataset.</summary>
        public void AddContractDataset(Guid internalRef) => _contractDatasets[internalRef] = 0;

        public void Configure(Mock<FakeDdbWrapper> ddb)
        {
            ddb.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
                .Returns((string ddbPath, string key, string? path) => GetRootMeta(ddbPath, key, path));
            ddb.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsIn(FixtureMetaKeys), It.IsAny<string?>()))
                .CallBase();

            ddb.Setup(d => d.MetaSet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.Is<string?>(p => string.IsNullOrEmpty(p))))
                .Returns((string ddbPath, string key, string data, string? _) => SetRootMeta(ddbPath, key, data));
            ddb.Setup(d => d.MetaSet(It.IsAny<string>(), It.IsIn(FixtureMetaKeys), It.IsAny<string>(),
                    It.IsAny<string?>()))
                .CallBase();

            ddb.Setup(d => d.Info(It.IsAny<string>(), false, 0, false))
                .Returns((string path, bool _, int _, bool _) => [RootInfo(path)]);

            ddb.Setup(d => d.ListEntries(It.IsAny<string>(), It.IsAny<EntryQuery>()))
                .Returns((string ddbPath, EntryQuery query) =>
                    Query(ddbPath, query, () => _fixtures.ListEntries(ddbPath, EntryQuery.All)));
            ddb.Setup(d => d.ListEntrySummaries(It.IsAny<string>(), It.IsAny<EntryQuery>()))
                .Returns((string ddbPath, EntryQuery query) =>
                    Query(ddbPath, query, () => _fixtures.ListEntrySummaries(ddbPath, EntryQuery.All)));

            ddb.Setup(d => d.GetEntry(It.IsAny<string>(), It.IsAny<string>()))
                .Returns((string ddbPath, string path) => GetEntry(ddbPath, path));

            // The stamp libddb returns: the checksum of the dataset, the (path, hash) pairs and the meta ids
            ddb.Setup(d => d.GetStamp(It.IsAny<string>())).Returns(new Stamp
            {
                Checksum = "4c9f05cb1d7e8a2f6b3d0e91a7c5f2d84b6e0a39",
                Entries =
                [
                    new Dictionary<string, string> { ["path"] = "ortho.tif", ["hash"] = "8f4e2a1c" },
                    new Dictionary<string, string> { ["path"] = "sub/img.jpg", ["hash"] = "1b7d3e90" }
                ],
                Meta = ["0a223495-84a0-4c15-b425-c7ef88110e75"]
            });
        }

        /// <summary>libddb <c>listEntries</c> over the whole contract dataset.</summary>
        private List<T> Query<T>(string ddbPath, EntryQuery query, Func<List<T>> wholeDataset)
            where T : EntrySummary
        {
            if (!IsContractDataset(ddbPath))
                return [];

            var all = wholeDataset();
            var folder = Generic(query.Path ?? "").Trim('/');
            if (folder == ".")
                folder = "";

            IEnumerable<T> entries = all;
            if (folder.Length > 0)
            {
                var self = all.FirstOrDefault(e => e.Path == folder);
                if (self != null && self.Type != EntryType.Directory)
                    entries = entries.Where(e => e.Path == folder);
                else
                {
                    entries = entries.Where(e => e.Path.StartsWith(folder + "/", StringComparison.Ordinal));
                    if (!query.Recursive)
                        entries = entries.Where(e => e.Depth == folder.Count(c => c == '/') + 1);
                }
            }
            else if (!query.Recursive)
                entries = entries.Where(e => e.Depth == 0);

            if (query.Types is { Count: > 0 } types)
                entries = entries.Where(e => types.Contains(e.Type));

            if (!string.IsNullOrEmpty(query.NamePattern))
            {
                var glob = new Regex(
                    "^" + Regex.Escape(query.NamePattern)
                        .Replace(@"\*", ".*", StringComparison.Ordinal)
                        .Replace(@"\?", ".", StringComparison.Ordinal) + "$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
                entries = entries.Where(e => glob.IsMatch(Path.GetFileName(e.Path)));
            }

            return entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        }

        private Entry? GetEntry(string ddbPath, string path)
        {
            try
            {
                return _fixtures.GetEntry(ddbPath, path);
            }
            catch (NotSupportedException)
            {
                // Indexed, but no get-* fixture: the entry listing fixture holds the same entry JSON
                var relative = Generic(path).Trim('/');
                return _fixtures.ListEntries(ddbPath, EntryQuery.All).Single(e => e.Path == relative);
            }
        }

        private ConcurrentDictionary<string, JsonNode?> MetaOf(string ddbPath) =>
            _rootMeta.GetOrAdd(Generic(ddbPath), _ =>
            {
                var meta = new ConcurrentDictionary<string, JsonNode?>(StringComparer.Ordinal);
                if (IsContractDataset(ddbPath))
                {
                    meta["name"] = JsonValue.Create("Golden dataset");
                    meta["visibility"] = JsonValue.Create((int)Visibility.Private);
                }

                return meta;
            });

        private string GetRootMeta(string ddbPath, string key, string? path)
        {
            if (!string.IsNullOrEmpty(path) || !MetaOf(ddbPath).TryGetValue(key, out var data))
                throw new DdbException($"Key not found: {key}");

            return MetaItem(key, data).ToJsonString(NativeJson.Options);
        }

        private Meta SetRootMeta(string ddbPath, string key, string data)
        {
            var value = ParseMetaData(data);
            MetaOf(ddbPath)[key] = value;
            return MetaItem(key, value).Deserialize<Meta>(NativeJson.Options)!;
        }

        /// <summary>libddb <c>MetaManager::validateData</c>: JSON as is, any other text is a JSON string.</summary>
        private static JsonNode? ParseMetaData(string data)
        {
            try
            {
                return JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                return JsonValue.Create(data);
            }
        }

        private static JsonObject MetaItem(string key, JsonNode? data) => new()
        {
            ["data"] = data?.DeepClone(),
            ["id"] = FakeDdbWrapper.MetaId("", key, data),
            ["mtime"] = FixtureMtime
        };

        private Entry RootInfo(string folder)
        {
            var indexed = IsContractDataset(folder)
                ? ContractFixture.Load("list").Output.AsArray().OfType<JsonObject>().ToList()
                : [];

            var meta = new JsonObject();
            foreach (var (key, data) in MetaOf(folder).OrderBy(m => m.Key, StringComparer.Ordinal))
                meta[key] = MetaItem(key, data);

            var entry = new JsonObject
            {
                ["depth"] = FakeDdbWrapper.PathDepth(folder),
                ["mtime"] = FixtureMtime,
                ["path"] = "file://" + Generic(folder),
                ["properties"] = new JsonObject { ["entries"] = indexed.Count, ["meta"] = meta },
                ["size"] = indexed.Sum(e => (long?)e["size"] ?? 0),
                ["type"] = (int)EntryType.DroneDB
            };

            // Same as the native adapter: NativeJson's contract and options
            return entry.Deserialize<Entry>(NativeJson.Options)!;
        }

        private bool IsContractDataset(string folder) =>
            Guid.TryParse(Path.GetFileName(Generic(folder)), out var internalRef) &&
            _contractDatasets.ContainsKey(internalRef);

        private static string Generic(string path) => path.Replace('\\', '/').TrimEnd('/');
    }
}
