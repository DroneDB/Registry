using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Ports.DroneDB;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.BuildNotifications;

/// <summary>
/// Whether a user may read a dataset outside a request (the digest of the build notifications), through the API's access
/// rules over a real (SQLite) Registry database and identity database read directly, as on a processing node: members,
/// the owner, an administrator and, for a public dataset, anybody may; a user removed from the organization, a
/// deactivated user and an unknown user may not read a private one.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~IdentityUserDatasetAccessTests"
/// </summary>
[TestFixture]
public class IdentityUserDatasetAccessTests : IDisposable
{
    private const string Org = "org";
    private const string Private = "private-ds";
    private const string Public = "public-ds";

    private TestDb<RegistryContext> _registry = null!;
    private SqliteConnection _identityConnection = null!;
    private Mock<ICacheManager> _cache = null!;

    [SetUp]
    public async Task SetUp()
    {
        _registry = TestDb.Create<RegistryContext>();
        await using (var db = _registry.NewContext())
        {
            var organization = new Organization { Slug = Org, Name = Org, OwnerId = "owner", CreationDate = DateTime.UtcNow };
            organization.Datasets =
            [
                new Dataset { Slug = Private, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow },
                new Dataset { Slug = Public, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow }
            ];
            organization.Users =
            [
                new OrganizationUser { UserId = "member", OrganizationSlug = Org, Permissions = OrganizationPermissions.ReadOnly },
                new OrganizationUser { UserId = "writer", OrganizationSlug = Org, Permissions = OrganizationPermissions.ReadWrite },
                new OrganizationUser { UserId = "deactivated", OrganizationSlug = Org }
            ];
            db.Organizations.Add(organization);
            await db.SaveChangesAsync();

            _cache = new Mock<ICacheManager>();
            foreach (var dataset in organization.Datasets)
                _cache.Setup(c => c.GetAsync(MagicStrings.DatasetVisibilityCacheSeed, Org,
                        It.Is<object[]>(a => a.Length == 3 && a[1].Equals(dataset.InternalRef))))
                    .ReturnsAsync(BitConverter.GetBytes((int)(dataset.Slug == Public ? Visibility.Public : Visibility.Private)));
        }

        _identityConnection?.Dispose();
        _identityConnection = new SqliteConnection("Data Source=:memory:");
        await _identityConnection.OpenAsync();
        await using var identity = Identity();
        await identity.Database.EnsureCreatedAsync();
        identity.Roles.AddRange(new IdentityRole("admin") { Id = "r-admin", NormalizedName = "ADMIN" },
            new IdentityRole("deactivated") { Id = "r-deactivated", NormalizedName = "DEACTIVATED" });
        foreach (var name in new[] { "owner", "member", "writer", "removed", "admin", "deactivated" })
            identity.Users.Add(new User { Id = name, UserName = name, NormalizedUserName = name.ToUpperInvariant() });
        identity.UserRoles.Add(new IdentityUserRole<string> { UserId = "admin", RoleId = "r-admin" });
        identity.UserRoles.Add(new IdentityUserRole<string> { UserId = "deactivated", RoleId = "r-deactivated" });
        await identity.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _registry.Dispose();
        _identityConnection.Dispose();
    }

    public void Dispose()
    {
        _registry?.Dispose();
        _identityConnection?.Dispose();
        GC.SuppressFinalize(this);
    }

    private ApplicationDbContext Identity() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_identityConnection).Options);

    private Task<bool> CanRead(string userId, string dsSlug, bool withIdentity = true) =>
        new IdentityUserDatasetAccess(_registry.NewContext(), Mock.Of<IDdbManager>(), _cache.Object,
                NullLogger<IdentityUserDatasetAccess>.Instance, withIdentity ? Identity() : null)
            .CanReadAsync(userId, Org, dsSlug, CancellationToken.None);

    [TestCase("member", Private, true, TestName = "A member reads a private dataset")]
    [TestCase("owner", Private, true, TestName = "The owner reads a private dataset")]
    [TestCase("admin", Private, true, TestName = "An administrator reads any dataset")]
    [TestCase("removed", Private, false, TestName = "A user removed from the organization no longer reads a private dataset")]
    [TestCase("removed", Public, true, TestName = "Anybody reads a public dataset")]
    [TestCase("deactivated", Private, false, TestName = "A deactivated member reads nothing")]
    [TestCase("unknown", Private, false, TestName = "An unknown user reads no private dataset")]
    public async Task TheAccessRulesOfTheApi_Apply(string userId, string dsSlug, bool canRead)
    {
        (await CanRead(userId, dsSlug)).ShouldBe(canRead);
    }

    [Test]
    public async Task ADatasetDeletedSince_IsReadByNobody()
    {
        (await CanRead("owner", "gone")).ShouldBeFalse();
    }

    [Test]
    public async Task AHostWithoutTheIdentityDatabase_CannotTell_AndAnswersNo()
    {
        (await CanRead("member", Private, withIdentity: false)).ShouldBeFalse();
    }

    // The write access to an organization of the user a task runs for (photogrammetry creating its dataset in the
    // worker, D-031): the organization rules of the API, without a request
    private Task<bool> CanWriteOrganization(string userId, string orgSlug = Org, bool withIdentity = true) =>
        new IdentityUserDatasetAccess(_registry.NewContext(), Mock.Of<IDdbManager>(), _cache.Object,
                NullLogger<IdentityUserDatasetAccess>.Instance, withIdentity ? Identity() : null)
            .CanAccessOrganizationAsync(userId, orgSlug, AccessType.Write, CancellationToken.None);

    [TestCase("writer", true, TestName = "A member with write permission writes to the organization")]
    [TestCase("owner", true, TestName = "The owner writes to the organization")]
    [TestCase("admin", true, TestName = "An administrator writes to any organization")]
    [TestCase("member", false, TestName = "A read-only member does not write to the organization")]
    [TestCase("removed", false, TestName = "A user removed from the organization does not write to it")]
    [TestCase("deactivated", false, TestName = "A deactivated member does not write to the organization")]
    [TestCase("unknown", false, TestName = "An unknown user does not write to the organization")]
    public async Task TheOrganizationWriteRulesOfTheApi_Apply(string userId, bool canWrite)
    {
        (await CanWriteOrganization(userId)).ShouldBe(canWrite);
    }

    [Test]
    public async Task AnOrganizationDeletedSince_IsWrittenByNobody()
    {
        (await CanWriteOrganization("admin", "gone")).ShouldBeFalse();
    }

    [Test]
    public async Task OrganizationWrite_OnAHostWithoutTheIdentityDatabase_CannotTell_AndAnswersNo()
    {
        (await CanWriteOrganization("owner", withIdentity: false)).ShouldBeFalse();
    }
}
