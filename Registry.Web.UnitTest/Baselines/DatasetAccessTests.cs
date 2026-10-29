using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The callers of the access tests and the seeded private organization they act on: an owner, a read-only
/// member, a read-write member, a stranger (signed in, no membership), the default administrator, a user
/// in the admin role whose token does not say admin ("promoted" after signing in) and a user whose token
/// says admin but who is not in the admin role ("demoted" while the token is still valid).
/// </summary>
public abstract class DatasetAccessFixture : IDisposable
{
    protected const string ProblemJson = "application/problem+json";
    protected const string OwnerUser = "datasetaccess-owner";
    protected const string ReaderUser = "datasetaccess-reader";
    protected const string WriterUser = "datasetaccess-writer";
    protected const string StrangerUser = "datasetaccess-stranger";
    protected const string PromotedUser = "datasetaccess-promoted";
    protected const string DemotedUser = "datasetaccess-demoted";
    protected const string PrivateOrg = "datasetaccess-private-org";
    protected const string PrivateDs = "datasetaccess-private-ds";

    public enum Caller
    {
        Anonymous,
        Stranger,
        Reader,
        Writer,
        Owner,
        Admin,

        /// <summary>Admin role, token claim admin=false.</summary>
        Promoted,

        /// <summary>No admin role, token claim admin=true.</summary>
        Demoted
    }

    protected RegistryWebFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected Dictionary<string, string> UserIds { get; } = new();

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Factory = new RegistryWebFactory(FactorySettings);

        // A dataset without visibility metadata is private (native ddb throws on a missing key)
        Factory.DdbWrapper.Setup(d => d.MetaGet(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new DdbException("Key not found"));

        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var name in new[] { OwnerUser, ReaderUser, WriterUser, StrangerUser, DemotedUser })
            UserIds[name] = (await Factory.CreateUserAsync(name)).Id;
        UserIds[PromotedUser] = (await Factory.CreateUserAsync(PromotedUser, isAdmin: true)).Id;

        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        var org = Seed.Organization(context, PrivateOrg, ownerId: UserIds[OwnerUser], isPublic: false);
        Seed.Dataset(context, org, PrivateDs);
        org.Users = new List<OrganizationUser>
        {
            new() { Organization = org, OrganizationSlug = PrivateOrg, UserId = UserIds[ReaderUser], Permissions = OrganizationPermissions.ReadOnly },
            new() { Organization = org, OrganizationSlug = PrivateOrg, UserId = UserIds[WriterUser], Permissions = OrganizationPermissions.ReadWrite }
        };
        await context.SaveChangesAsync();

        await SeedAsync(context);
    }

    /// <summary>Settings of the fixture's host (null: the defaults).</summary>
    protected virtual IDictionary<string, string>? FactorySettings => null;

    /// <summary>More seed data of a fixture (saved by the fixture).</summary>
    protected virtual Task SeedAsync(RegistryContext context) => Task.CompletedTask;

    public void Dispose()
    {
        Client?.Dispose();
        Factory?.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static HttpRequestMessage Request(HttpMethod method, string url, Caller caller,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        return caller switch
        {
            Caller.Anonymous => request,
            Caller.Stranger => request.AsUser(StrangerUser),
            Caller.Reader => request.AsUser(ReaderUser),
            Caller.Writer => request.AsUser(WriterUser),
            Caller.Owner => request.AsUser(OwnerUser),
            Caller.Admin => request.AsUser(RegistryWebFactory.AdminUserName, isAdmin: true),
            Caller.Promoted => request.AsUser(PromotedUser, isAdmin: false),
            Caller.Demoted => request.AsUser(DemotedUser, isAdmin: true),
            _ => throw new ArgumentOutOfRangeException(nameof(caller))
        };
    }

    protected async Task<(HttpResponseMessage Response, string Body, JsonObject? Json)> SendAsync(
        HttpRequestMessage request)
    {
        var response = await Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        var json = body.TrimStart().StartsWith('{') ? JsonNode.Parse(body) as JsonObject : null;
        return (response, body, json);
    }

    protected static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    protected static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    protected static void ShouldBeProblem(HttpResponseMessage response, JsonObject? problem, int status, string detail)
    {
        ((int)response.StatusCode).ShouldBe(status);
        response.Content.Headers.ContentType?.MediaType.ShouldBe(ProblemJson);
        problem.ShouldNotBeNull();
        problem["status"]!.GetValue<int>().ShouldBe(status);
        problem["detail"]!.GetValue<string>().ShouldBe(detail);
        problem["noRetry"]!.GetValue<bool>().ShouldBeTrue();
    }

    protected async Task<T> WithContextAsync<T>(Func<RegistryContext, Task<T>> query)
    {
        using var scope = Factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<RegistryContext>());
    }
}

/// <summary>
/// Regression guard: who may use representative endpoints of every controller with an access check
/// (organizations, datasets, objects, meta, tasks, STAC, MVT, OGC, push) on a private dataset. The table
/// holds the statuses the endpoints answered before the access checks moved into the resolver and the filters:
/// that move must not make any endpoint more permissive (or less, for a legitimate caller).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetAccessPermissionMatrixTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class DatasetAccessPermissionMatrixTests : DatasetAccessFixture
{
    private const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;

    /// <summary>Expected for a caller who passes the access check: anything but 401/403 (the strict
    /// <c>FakeDdbWrapper</c> answers only its contract fixtures, so the action itself may fail).</summary>
    private const int Passes = 0;

    // Read: anonymous 401, stranger 403, every member, the owner and the admin pass
    private static readonly Dictionary<Caller, int> Read = new()
    {
        [Caller.Anonymous] = 401, [Caller.Stranger] = 403, [Caller.Reader] = Passes, [Caller.Writer] = Passes,
        [Caller.Owner] = Passes, [Caller.Admin] = Passes
    };

    // Write: a read-only member is refused too
    private static readonly Dictionary<Caller, int> Write = new()
    {
        [Caller.Anonymous] = 401, [Caller.Stranger] = 403, [Caller.Reader] = 403, [Caller.Writer] = Passes,
        [Caller.Owner] = Passes, [Caller.Admin] = Passes
    };

    // Delete: only the owner and the admin (ReadWrite members cannot delete)
    private static readonly Dictionary<Caller, int> Delete = new()
    {
        [Caller.Anonymous] = 401, [Caller.Stranger] = 403, [Caller.Reader] = 403, [Caller.Writer] = 403
    };

    private sealed record Row(string Name, HttpMethod Method, string Url, Dictionary<Caller, int> Expected,
        Func<HttpContent>? Body = null);

    private static readonly Row[] Rows =
    [
        new("OrganizationsController.Get", HttpMethod.Get, "/orgs/" + PrivateOrg, Read),
        new("OrganizationsController.Delete", HttpMethod.Delete, "/orgs/" + PrivateOrg, Delete),
        new("DatasetsController.GetAll", HttpMethod.Get, "/orgs/" + PrivateOrg + "/ds", Read),
        new("DatasetsController.Get", HttpMethod.Get, Ds, Read),
        new("DatasetsController.GetStamp", HttpMethod.Get, Ds + "/stamp", Read),
        new("DatasetsController.Delete", HttpMethod.Delete, Ds, Delete),
        new("ObjectsController.GetInfo", HttpMethod.Get, Ds + "/list", Read),
        new("ObjectsController.GetPendingBuilds", HttpMethod.Get, Ds + "/builds", Read),
        new("ObjectsController.GetRasterInfo", HttpMethod.Get, Ds + "/raster-info?path=a.tif", Read),
        new("ObjectsController.GenerateThumbnail", HttpMethod.Get, Ds + "/thumb?path=a.jpg", Read),
        new("MetaController.List", HttpMethod.Get, Ds + "/meta/list", Read),
        new("MetaController.SetAlt", HttpMethod.Post, Ds + "/meta/set", Write,
            () => Form(("key", "datasetaccess"), ("data", "\"value\""))),
        new("TasksController.List", HttpMethod.Get, Ds + "/tasks", Read),
        new("TasksController.Clear", HttpMethod.Post, Ds + "/tasks/clear", Write),
        new("StacController.GetDatasetStac", HttpMethod.Get, Ds + "/stac", Read),
        new("MvtController.GetMetadata", HttpMethod.Get, Ds + "/mvt/abc/metadata.json", Read),
        new("WmsController.Get", HttpMethod.Get, Ds + "/wms?SERVICE=WMS&REQUEST=GetCapabilities", Read),
        new("PushController.Init", HttpMethod.Post, Ds + "/push/init", Write,
            () => Form(("checksum", "x"), ("stamp", "{}"), ("stampJson", "{}")))
    ];

    public static IEnumerable<TestCaseData> Cases() =>
        from row in Rows
        from expected in row.Expected
        select new TestCaseData(row.Name, expected.Key)
            .SetName($"{row.Name}_{expected.Key}_{(expected.Value == Passes ? "Passes" : "Is" + expected.Value)}");

    [TestCaseSource(nameof(Cases))]
    public async Task Endpoint_AnswersTheCallerAsBefore(string rowName, Caller caller)
    {
        var row = Rows.Single(r => r.Name == rowName);

        var (response, body, _) = await SendAsync(Request(row.Method, row.Url, caller, row.Body?.Invoke()));

        var status = (int)response.StatusCode;
        var because = $"{row.Method} {row.Url} as {caller}: {Truncate(body)}";
        if (row.Expected[caller] == Passes)
            status.ShouldNotBeOneOf([401, 403], because);
        else
            status.ShouldBe(row.Expected[caller], because);
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";
}

/// <summary>
/// Access behaviour, through HTTP. Each rule is one behaviour:
/// <list type="bullet">
/// <item>One denial message per rule (read/write/delete × dataset/organization), <c>noRetry: true</c>, from
/// <c>IDatasetAccessResolver</c>.</item>
/// <item>The OGC authorization filter (now also on MVT) maps client errors through the classifier.</item>
/// </list>
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetAccessBehaviourTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class DatasetAccessBehaviourTests : DatasetAccessFixture
{
    private const string Ds = "/orgs/" + PrivateOrg + "/ds/" + PrivateDs;

    // ---- One message per access rule ----

    [Test]
    public async Task DatasetRead_Denied_HasTheReadMessage()
    {
        var (response, _, problem) = await SendAsync(Request(HttpMethod.Get, Ds, Caller.Stranger));
        ShouldBeProblem(response, problem, 403, "The current user is not allowed to read this dataset");

        // the same rule answers an anonymous caller 401
        (response, _, problem) = await SendAsync(Request(HttpMethod.Get, Ds, Caller.Anonymous));
        ShouldBeProblem(response, problem, 401, "The current user is not allowed to read this dataset");
    }

    [Test]
    public async Task DatasetWrite_Denied_HasTheWriteMessageAndNoRetry()
    {
        // Was "Access denied" with noRetry false (TasksManager's own denial)
        var (response, _, problem) = await SendAsync(Request(HttpMethod.Post, Ds + "/tasks/clear", Caller.Reader));

        ShouldBeProblem(response, problem, 403, "The current user is not allowed to write to this dataset");
    }

    [Test]
    public async Task DatasetDelete_Denied_HasTheDeleteMessage()
    {
        var (response, _, problem) = await SendAsync(Request(HttpMethod.Delete, Ds, Caller.Writer));

        ShouldBeProblem(response, problem, 403, "The current user is not allowed to delete this dataset");
    }

    [Test]
    public async Task OrganizationRead_Denied_HasTheOrganizationMessage()
    {
        // Was "Invalid user"
        var (response, _, problem) = await SendAsync(Request(HttpMethod.Get, "/orgs/" + PrivateOrg, Caller.Stranger));

        ShouldBeProblem(response, problem, 403, "The current user is not allowed to read this organization");
    }

    // ---- OGC authorization filter (and MVT) ----

    [Test]
    public async Task OgcFilter_InvalidSlug_Is400()
    {
        // The route accepts "a--b", the slug rule does not: GetDataset threw BadRequestException and the
        // filter's catch-all made it a 500
        var (response, body, _) = await SendAsync(Request(HttpMethod.Get,
            "/orgs/datasetaccess--bad/ds/datasetaccess-ds/wms?SERVICE=WMS&REQUEST=GetCapabilities", Caller.Anonymous));

        ((int)response.StatusCode).ShouldBe(400, body);
    }

    [Test]
    public async Task Mvt_UsesTheOgcFilter_OneBasicChallengeForAnonymous()
    {
        var url = Ds + "/mvt/abc/metadata.json";

        var (anonymous, body, _) = await SendAsync(Request(HttpMethod.Get, url, Caller.Anonymous));
        ((int)anonymous.StatusCode).ShouldBe(401);
        anonymous.Headers.WwwAuthenticate.Count(h => h.Scheme == "Basic").ShouldBe(1);
        anonymous.Content.Headers.ContentType?.MediaType.ShouldBe("text/xml");
        body.ShouldContain("AuthenticationFailed");

        var (stranger, strangerBody, _) = await SendAsync(Request(HttpMethod.Get, url, Caller.Stranger));
        ((int)stranger.StatusCode).ShouldBe(403);
        stranger.Headers.WwwAuthenticate.ShouldBeEmpty();
        strangerBody.ShouldContain("AccessDenied");
    }
}

/// <summary>
/// Admin gate, through HTTP: every admin-only endpoint declares the <c>Admin</c> policy, which needs
/// the token's admin claim and the admin role in the identity store; a denial is a ProblemDetails 403 with
/// one message; a non-admin cannot half-update a user.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~AdminOnlyPolicyTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class AdminOnlyPolicyTests : DatasetAccessFixture
{
    private const string AdminMessage = "Only administrators can perform this operation";

    // ---- The Admin policy ----

    private static readonly (HttpMethod Method, string Url)[] AdminEndpoints =
    [
        (HttpMethod.Get, "/users"),
        (HttpMethod.Get, "/users/detailed"),
        (HttpMethod.Get, "/users/" + StrangerUser + "/orgs"),
        (HttpMethod.Get, "/users/" + StrangerUser + "/meta"),
        (HttpMethod.Get, "/users/" + StrangerUser + "/storage"),
        (HttpMethod.Get, "/sys/report"),
        (HttpMethod.Get, "/sys/build-pending-status"),
        (HttpMethod.Get, "/sys/tasks"),
        (HttpMethod.Get, "/sys/config")
    ];

    [Test]
    public async Task AdminEndpoints_NonAdmin_Is403ProblemWithOneMessage()
    {
        foreach (var (method, url) in AdminEndpoints)
        {
            var (response, _, problem) = await SendAsync(Request(method, url, Caller.Stranger));
            ShouldBeProblem(response, problem, 403, AdminMessage);
        }
    }

    [Test]
    public async Task AdminEndpoints_AdminRoleWithoutTheAdminClaim_Is403()
    {
        // The token predates the promotion: the Hub does not show the admin pages either
        foreach (var (method, url) in AdminEndpoints)
        {
            var (response, body, _) = await SendAsync(Request(method, url, Caller.Promoted));
            ((int)response.StatusCode).ShouldBe(403, $"{method} {url}: {body}");
        }
    }

    [Test]
    public async Task AdminEndpoints_AdminClaimWithoutTheAdminRole_Is403()
    {
        // A demoted administrator whose token is still valid (GET /sys/config used to read the claim only)
        foreach (var (method, url) in AdminEndpoints)
        {
            var (response, body, _) = await SendAsync(Request(method, url, Caller.Demoted));
            ((int)response.StatusCode).ShouldBe(403, $"{method} {url}: {body}");
        }
    }

    [Test]
    public async Task AdminEndpoints_AnonymousIsChallenged_AdminPasses()
    {
        // Regression guard: the gate itself
        foreach (var (method, url) in AdminEndpoints)
        {
            var (anonymous, _, _) = await SendAsync(Request(method, url, Caller.Anonymous));
            ((int)anonymous.StatusCode).ShouldBe(401, $"{method} {url}");

            // Past the gate (the strict FakeDdbWrapper may fail the action itself, e.g. dataset sizes)
            var (admin, body, _) = await SendAsync(Request(method, url, Caller.Admin));
            ((int)admin.StatusCode).ShouldNotBeOneOf([401, 403], $"{method} {url}: {body}");
        }
    }

    // Commands: the gate answers before the body is read or anything changes, so no admin call here
    private static readonly (HttpMethod Method, string Url)[] AdminCommandEndpoints =
    [
        (HttpMethod.Post, "/users"),
        (HttpMethod.Delete, "/users"),
        (HttpMethod.Delete, "/users/" + StrangerUser),
        (HttpMethod.Put, "/users/" + StrangerUser),
        (HttpMethod.Post, "/users/" + StrangerUser + "/meta"),
        (HttpMethod.Post, "/users/" + StrangerUser + "/orgs"),
        (HttpMethod.Put, "/users/" + StrangerUser + "/orgs/" + PrivateOrg),
        (HttpMethod.Delete, "/users/" + StrangerUser + "/orgs/" + PrivateOrg),
        (HttpMethod.Post, "/users/roles"),
        (HttpMethod.Delete, "/users/roles/datasetaccess"),
        (HttpMethod.Post, "/sys/cleanupbatches"),
        (HttpMethod.Post, "/sys/cleanupdatasets"),
        (HttpMethod.Post, "/sys/cleanup"),
        (HttpMethod.Post, "/sys/rescan"),
        (HttpMethod.Post, "/sys/migratevisibility"),
        (HttpMethod.Post, "/sys/fix-invalid-slugs"),
        (HttpMethod.Post, "/sys/import-dataset"),
        (HttpMethod.Post, "/sys/import-organization"),
        (HttpMethod.Post, "/sys/move-datasets"),
        (HttpMethod.Post, "/sys/merge-organizations"),
        (HttpMethod.Post, "/sys/cleanup-jobindices"),
        (HttpMethod.Post, "/sys/tasks/datasetaccess/delete")
    ];

    [Test]
    public async Task AdminCommandEndpoints_NonAdminCallers_AreRefusedByTheGate()
    {
        foreach (var (method, url) in AdminCommandEndpoints)
        {
            var (anonymous, _, _) = await SendAsync(Request(method, url, Caller.Anonymous));
            ((int)anonymous.StatusCode).ShouldBe(401, $"{method} {url}");

            var (stranger, _, problem) = await SendAsync(Request(method, url, Caller.Stranger));
            ShouldBeProblem(stranger, problem, 403, AdminMessage);

            foreach (var caller in new[] { Caller.Promoted, Caller.Demoted })
            {
                var (response, body, _) = await SendAsync(Request(method, url, caller));
                ((int)response.StatusCode).ShouldBe(403, $"{method} {url} as {caller}: {body}");
            }
        }
    }

    [Test]
    public async Task UpdateUser_NonAdminSelf_IsRefusedBeforeAnyChange()
    {
        // PUT /users/{userName} updates the e-mail and the roles: admin only. It used to commit the
        // e-mail of a non-admin's own user and then answer 403 on the roles.
        var request = Request(HttpMethod.Put, "/users/" + StrangerUser, Caller.Stranger,
            Json("{\"email\":\"changed@example.com\",\"roles\":[]}"));

        var (response, _, problem) = await SendAsync(request);

        ShouldBeProblem(response, problem, 403, AdminMessage);
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Registry.Web.Identity.Models.User>>();
        (await users.FindByNameAsync(StrangerUser))!.Email.ShouldBe(StrangerUser + "@example.com");
    }
}

/// <summary>
/// Organization membership, through HTTP: one implementation (<c>OrganizationMembershipService</c>)
/// with the same codes on the admin and organization routes, <c>GrantedAt</c>/<c>GrantedBy</c> on every
/// grant, merges that keep members and their permissions, and organization edits that keep the owner.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~OrganizationMembershipAccessTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public sealed class OrganizationMembershipAccessTests : DatasetAccessFixture
{
    private const string MembershipOrg = "datasetaccess-membership-org";
    private const string MergeSourceOrg = "datasetaccess-merge-source";
    private const string MergeDestOrg = "datasetaccess-merge-dest";
    private const string EditOrg = "datasetaccess-edit-org";
    private const string LegacyMember = "datasetaccess-legacy-member";

    // The organization member routes are off by default
    protected override IDictionary<string, string>? FactorySettings => new Dictionary<string, string>
    {
        ["AppSettings:EnableOrganizationMemberManagement"] = "true"
    };

    protected override async Task SeedAsync(RegistryContext context)
    {
        UserIds[LegacyMember] = (await Factory.CreateUserAsync(LegacyMember)).Id;

        // A membership recorded before GrantedAt/GrantedBy existed (both null)
        var membership = Seed.Organization(context, MembershipOrg, ownerId: UserIds[OwnerUser]);
        membership.Users = new List<OrganizationUser>
        {
            new() { Organization = membership, OrganizationSlug = MembershipOrg, UserId = UserIds[LegacyMember], Permissions = OrganizationPermissions.ReadOnly }
        };

        var source = Seed.Organization(context, MergeSourceOrg, ownerId: UserIds[OwnerUser]);
        source.Users = new List<OrganizationUser>
        {
            new() { Organization = source, OrganizationSlug = MergeSourceOrg, UserId = UserIds[ReaderUser], Permissions = OrganizationPermissions.ReadOnly },
            new() { Organization = source, OrganizationSlug = MergeSourceOrg, UserId = UserIds[WriterUser], Permissions = OrganizationPermissions.Admin }
        };
        Seed.Organization(context, MergeDestOrg, ownerId: UserIds[StrangerUser]);

        var edit = Seed.Organization(context, EditOrg, ownerId: UserIds[OwnerUser]);
        edit.Users = new List<OrganizationUser>
        {
            new() { Organization = edit, OrganizationSlug = EditOrg, UserId = UserIds[WriterUser], Permissions = OrganizationPermissions.ReadWrite }
        };

        await context.SaveChangesAsync();
    }

    // ---- Membership ----

    [Test]
    public async Task AdminMembershipRoutes_UseTheOrganizationRouteCodes()
    {
        async Task<int> Status(HttpMethod method, string url, string? json = null) =>
            (int)(await SendAsync(Request(method, url, Caller.Admin, json == null ? null : Json(json)))).Response.StatusCode;

        // Missing organization, missing user, already a member, not a member: 400 before
        (await Status(HttpMethod.Post, "/users/" + StrangerUser + "/orgs", "{\"orgSlug\":\"datasetaccess-missing\",\"permissions\":0}")).ShouldBe(404);
        (await Status(HttpMethod.Post, "/users/datasetaccess-nobody/orgs", $"{{\"orgSlug\":\"{PrivateOrg}\",\"permissions\":0}}")).ShouldBe(404);
        (await Status(HttpMethod.Post, "/users/" + ReaderUser + "/orgs", $"{{\"orgSlug\":\"{PrivateOrg}\",\"permissions\":0}}")).ShouldBe(409);
        (await Status(HttpMethod.Delete, "/users/" + StrangerUser + "/orgs/" + PrivateOrg)).ShouldBe(404);
        (await Status(HttpMethod.Put, "/users/" + StrangerUser + "/orgs/" + PrivateOrg, "{\"permissions\":1}")).ShouldBe(404);
        // The owner: 400 on both routes, before and after
        (await Status(HttpMethod.Post, "/users/" + OwnerUser + "/orgs", $"{{\"orgSlug\":\"{PrivateOrg}\",\"permissions\":0}}")).ShouldBe(400);
    }

    [Test]
    public async Task OrganizationMemberRoutes_OwnerAsTarget_Is400LikeTheAdminRoutes()
    {
        // The owner is not a member: the organization routes answered 404 "not a member", the admin
        // routes 400; both answer 400 now
        var url = "/orgs/" + PrivateOrg + "/members/" + OwnerUser;

        var (update, updateBody, _) = await SendAsync(Request(HttpMethod.Put, url, Caller.Owner, Json("{\"permissions\":1}")));
        ((int)update.StatusCode).ShouldBe(400, updateBody);

        var (remove, removeBody, _) = await SendAsync(Request(HttpMethod.Delete, url, Caller.Owner));
        ((int)remove.StatusCode).ShouldBe(400, removeBody);

        var (admin, adminBody, _) = await SendAsync(Request(HttpMethod.Delete, "/users/" + OwnerUser + "/orgs/" + PrivateOrg, Caller.Admin));
        ((int)admin.StatusCode).ShouldBe(400, adminBody);
    }

    [Test]
    public async Task AdminMembershipRoute_PermissionChange_RecordsTheGrant()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);

        var (response, body, _) = await SendAsync(Request(HttpMethod.Put,
            "/users/" + LegacyMember + "/orgs/" + MembershipOrg, Caller.Admin, Json("{\"permissions\":2}")));

        ((int)response.StatusCode).ShouldBe(200, body);
        var row = await WithContextAsync(c => c.OrganizationsUsers.AsNoTracking()
            .SingleAsync(u => u.OrganizationSlug == MembershipOrg && u.UserId == UserIds[LegacyMember]));
        row.Permissions.ShouldBe(OrganizationPermissions.ReadWriteDelete);
        row.GrantedBy.ShouldBe(await AdminIdAsync());
        row.GrantedAt.ShouldNotBeNull();
        row.GrantedAt!.Value.ShouldBeGreaterThan(before);
    }

    [Test]
    public async Task Merge_KeepsEachMembersPermission()
    {
        var (response, body, _) = await SendAsync(Request(HttpMethod.Post, "/sys/merge-organizations", Caller.Admin,
            Json($"{{\"sourceOrgSlug\":\"{MergeSourceOrg}\",\"destinationOrgSlug\":\"{MergeDestOrg}\",\"deleteSourceOrganization\":false}}")));

        ((int)response.StatusCode).ShouldBe(200, body);
        var members = await WithContextAsync(c => c.OrganizationsUsers.AsNoTracking()
            .Where(u => u.OrganizationSlug == MergeDestOrg).ToListAsync());
        // Every transferred member used to get the entity default, ReadWrite
        members.Single(u => u.UserId == UserIds[ReaderUser]).Permissions.ShouldBe(OrganizationPermissions.ReadOnly);
        members.Single(u => u.UserId == UserIds[WriterUser]).Permissions.ShouldBe(OrganizationPermissions.Admin);
        members.ShouldAllBe(u => u.GrantedAt != null && u.GrantedBy != null);
    }

    [Test]
    public async Task EditOrganization_WithoutOwner_KeepsTheOwner()
    {
        // The Hub sends no owner: a read-write member and an administrator used to become the owner
        foreach (var caller in new[] { Caller.Writer, Caller.Admin })
        {
            var (response, body, _) = await SendAsync(Request(HttpMethod.Put, "/orgs/" + EditOrg, caller,
                Form(("slug", EditOrg), ("name", "Renamed by " + caller))));

            ((int)response.StatusCode).ShouldBe(204, body);
            var org = await WithContextAsync(c => c.Organizations.AsNoTracking().SingleAsync(o => o.Slug == EditOrg));
            org.OwnerId.ShouldBe(UserIds[OwnerUser], $"after an edit by {caller}");
            org.Name.ShouldBe("Renamed by " + caller);
        }
    }

    [Test]
    public async Task EditOrganization_MemberSettingItselfAsOwner_Is403()
    {
        var (response, _, problem) = await SendAsync(Request(HttpMethod.Put, "/orgs/" + EditOrg, Caller.Writer,
            Form(("slug", EditOrg), ("name", "Taken over"), ("owner", UserIds[WriterUser]))));

        ShouldBeProblem(response, problem, 403, "Only administrators can change the owner of an organization");
        (await WithContextAsync(c => c.Organizations.AsNoTracking().SingleAsync(o => o.Slug == EditOrg)))
            .OwnerId.ShouldBe(UserIds[OwnerUser]);
    }

    private async Task<string> AdminIdAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Registry.Web.Identity.Models.User>>();
        return (await users.FindByNameAsync(RegistryWebFactory.AdminUserName))!.Id;
    }
}
