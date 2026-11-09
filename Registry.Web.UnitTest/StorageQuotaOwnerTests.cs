using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// FR-XC-26, D-039: <see cref="IStorageQuota"/> is the quota of the owner of the target organization, whoever writes.
/// One <c>RemainingBytesAsync</c> applies <c>EnableStorageLimiter</c> and the administrator exemption of the owner; the
/// role of the user who writes does not matter and no current user is read.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~StorageQuotaOwnerTests"
/// </summary>
[TestFixture]
public class StorageQuotaOwnerTests
{
    private const long Mb = 1024 * 1024;

    private TestDb<RegistryContext> _registry = null!;
    private TestDb<ApplicationDbContext> _identity = null!;
    private RegistryContext _context = null!;
    private ApplicationDbContext _users = null!;
    private Mock<IDatasetSummaryProvider> _summaries = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = TestDb.Create<RegistryContext>();
        _identity = TestDb.Create<ApplicationDbContext>();
        _context = _registry.NewContext();
        _users = _identity.NewContext();
        _summaries = new Mock<IDatasetSummaryProvider>();
        // Every dataset is 10 MB
        _summaries.Setup(s => s.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(new DatasetSummary(10 * Mb, null, null));
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _users.Dispose();
        _registry.Dispose();
        _identity.Dispose();
    }

    private async Task<User> OwnerAsync(string id, long? maxStorageMb, bool admin = false)
    {
        var user = new User { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant() };
        if (maxStorageMb is { } mb)
            user.Metadata[MagicStrings.MaxStorageKey] = mb;
        _users.Users.Add(user);
        if (admin)
        {
            var role = new IdentityRole { Id = "admin-role", Name = ApplicationDbContext.AdminRoleName, NormalizedName = ApplicationDbContext.AdminRoleName.ToUpperInvariant() };
            _users.Roles.Add(role);
            _users.UserRoles.Add(new IdentityUserRole<string> { RoleId = role.Id, UserId = id });
        }

        await _users.SaveChangesAsync();
        return user;
    }

    private void Organization(string slug, string ownerId, int datasets)
    {
        var org = Seed.Organization(_context, slug, ownerId: ownerId);
        for (var i = 0; i < datasets; i++)
            Seed.Dataset(_context, org, $"ds{i}");
        _context.SaveChanges();
    }

    private StorageQuota Quota(bool limiter = true) => new(_context, _users,
        TestSettings.Options<AccountOptions>(Microsoft.Extensions.Options.Options.Create(new AppSettings { EnableStorageLimiter = limiter, DatasetsPath = "/tmp" })),
        _summaries.Object);

    [Test]
    public async Task Remaining_IsTheLimitOfTheOwnerMinusWhatTheirDatasetsUse()
    {
        await OwnerAsync("o1", maxStorageMb: 45);
        Organization("acme", "o1", datasets: 3); // 30 MB used

        (await Quota().RemainingBytesAsync("o1")).ShouldBe(15 * Mb);
    }

    [Test]
    public async Task Remaining_OfAnOwnerOverTheQuota_IsZero_NotNegative()
    {
        await OwnerAsync("o1", maxStorageMb: 20);
        Organization("acme", "o1", datasets: 4); // 40 MB used of 20

        (await Quota().RemainingBytesAsync("o1")).ShouldBe(0);
    }

    [Test]
    public async Task Remaining_WithTheLimiterOff_IsNull()
    {
        await OwnerAsync("o1", maxStorageMb: 1);
        Organization("acme", "o1", datasets: 3);

        (await Quota(limiter: false).RemainingBytesAsync("o1")).ShouldBeNull();
    }

    [Test]
    public async Task Remaining_OfAnAdministratorOwner_IsNull_TheExemptionIsTheOwners()
    {
        await OwnerAsync("admin-owner", maxStorageMb: 1, admin: true);
        Organization("acme", "admin-owner", datasets: 3);

        (await Quota().RemainingBytesAsync("admin-owner")).ShouldBeNull();
    }

    [Test]
    public async Task Remaining_OfAnOwnerWithoutALimit_IsNull()
    {
        await OwnerAsync("o1", maxStorageMb: null);
        Organization("acme", "o1", datasets: 3);

        (await Quota().RemainingBytesAsync("o1")).ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("nobody")]
    public async Task Remaining_WithoutAnOwner_IsNull(string? ownerId) =>
        (await Quota().RemainingBytesAsync(ownerId)).ShouldBeNull();

    [Test]
    public async Task OwnerIdOf_IsTheOwnerOfTheOrganization_NotTheOneWhoWrites()
    {
        await OwnerAsync("o1", maxStorageMb: 45);
        await OwnerAsync("member", maxStorageMb: null);
        Organization("acme", "o1", datasets: 1);

        (await Quota().OwnerIdOfAsync("acme")).ShouldBe("o1");
        (await Quota().OwnerIdOfAsync("missing")).ShouldBeNull();
        (await Quota().RemainingBytesOfOrganizationAsync("acme")).ShouldBe(35 * Mb);
    }

    [Test]
    public async Task Check_RefusesWhatDoesNotFit_WithThe507Text()
    {
        await OwnerAsync("o1", maxStorageMb: 45);
        Organization("acme", "o1", datasets: 3); // 15 MB left

        await Quota().CheckOwnerStorageAsync("o1", 15 * Mb);

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => Quota().CheckOwnerStorageAsync("o1", 15 * Mb + 1));
        ex.StatusCode.ShouldBe(507);
        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 15 MB left.");
    }

    [Test]
    public async Task Check_OfNothing_RefusesAnOwnerOverQuota_AsTheShareInitDoes()
    {
        await OwnerAsync("o1", maxStorageMb: 20);
        Organization("acme", "o1", datasets: 4);

        var ex = await Should.ThrowAsync<QuotaExceededException>(() => Quota().CheckOwnerStorageAsync("o1"));

        ex.Message.ShouldBe("Storage quota exceeded: the owner of this organization has 0 B left.");
    }
}
