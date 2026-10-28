#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Middlewares;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// Authorization facts (user exists, admin, deactivated) are cached across requests in
/// <see cref="UserAuthSnapshotCache"/>. A request with a warm snapshot makes no identity store
/// query (it used to make 3 for the owner and 5 for an organization member, every request);
/// role changes and deletions through <see cref="RegistryUserManager"/> invalidate it.
/// </summary>
[TestFixture]
public class AuthSnapshotTests
{
    private readonly User _owner = new() { Id = "owner", UserName = "owner" };
    private readonly User _member = new() { Id = "member", UserName = "member" };
    private Organization _org = null!;
    private Dataset _privateDataset = null!;
    private Dataset _publicDataset = null!;
    private Mock<UserManager<User>> _users = null!;
    private Mock<ICacheManager> _cache = null!;
    private UserAuthSnapshotCache _snapshots = null!;
    private HashSet<(string userId, string role)> _roles = null!;
    private int _identityQueries;

    [SetUp]
    public void Setup()
    {
        _identityQueries = 0;
        _roles = [];
        _org = new Organization
        {
            Slug = "org", OwnerId = _owner.Id, IsPublic = false,
            Users = new List<OrganizationUser>
            {
                new() { UserId = _member.Id, Permissions = OrganizationPermissions.ReadOnly }
            }
        };
        _privateDataset = new Dataset { Id = 1, Slug = "ds", Organization = _org, InternalRef = Guid.NewGuid() };
        _publicDataset = new Dataset { Id = 2, Slug = "pub", Organization = _org, InternalRef = Guid.NewGuid() };

        var store = new Mock<IUserStore<User>>();
        _users = new Mock<UserManager<User>>(store.Object, null!, null!, null!, null!, null!, null!, null!, null!);
        _users.Setup(u => u.FindByIdAsync(It.IsAny<string>())).Returns((string id) =>
        {
            _identityQueries++;
            return Task.FromResult(new[] { _owner, _member }.FirstOrDefault(x => x.Id == id));
        });
        _users.Setup(u => u.IsInRoleAsync(It.IsAny<User>(), It.IsAny<string>())).Returns((User u, string role) =>
        {
            _identityQueries++;
            return Task.FromResult(_roles.Contains((u.Id, role)));
        });

        _cache = new Mock<ICacheManager>();
        _cache.Setup(c => c.GetAsync(MagicStrings.DatasetVisibilityCacheSeed, It.IsAny<string>(),
                It.IsAny<object[]>()))
            .ReturnsAsync((string _, string _, object[] args) => BitConverter.GetBytes(
                (int)((Guid)args[1] == _publicDataset.InternalRef ? Visibility.Public : Visibility.Private)));

        _snapshots = new UserAuthSnapshotCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.FromMinutes(5));
    }

    // One request = one DI scope = one AuthManager (and one snapshot provider)
    private AuthManager Request(User? current)
    {
        var http = new Mock<IHttpContextAccessor>();
        http.Setup(h => h.HttpContext).Returns(current == null
            ? new DefaultHttpContext()
            : new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, current.Id)]))
            });

        return new AuthManager(_users.Object, http.Object, Mock.Of<IDdbManager>(), new Mock<RegistryContext>().Object,
            Mock.Of<ILogger<AuthManager>>(), _cache.Object, new UserAuthSnapshotProvider(_users.Object, _snapshots));
    }

    private async Task<int> QueriesOf(Func<Task> request)
    {
        var before = _identityQueries;
        await request();
        return _identityQueries - before;
    }

    [Test]
    public async Task RequestAccess_Member_WarmSnapshot_NoIdentityQueries()
    {
        var first = await QueriesOf(async () =>
            (await Request(_member).RequestAccess(_privateDataset, AccessType.Read)).ShouldBeTrue());
        var second = await QueriesOf(async () =>
            (await Request(_member).RequestAccess(_privateDataset, AccessType.Read)).ShouldBeTrue());

        TestContext.Out.WriteLine($"member/private/Read: first request {first}, second request {second} identity queries");
        first.ShouldBe(3, "one user lookup and two role checks fill the snapshot");
        second.ShouldBe(0);
        (await Request(_member).RequestAccess(_privateDataset, AccessType.Write)).ShouldBeFalse();
    }

    [Test]
    public async Task RequestAccess_Owner_WarmSnapshot_NoIdentityQueries()
    {
        await Request(_owner).RequestAccess(_privateDataset, AccessType.Delete);

        var second = await QueriesOf(async () =>
            (await Request(_owner).RequestAccess(_privateDataset, AccessType.Delete)).ShouldBeTrue());

        second.ShouldBe(0);
    }

    [Test]
    public async Task RequestAccess_AnonymousOnPublicDataset_OwnerSnapshotIsReused()
    {
        (await Request(null).RequestAccess(_publicDataset, AccessType.Read)).ShouldBeTrue();

        var second = await QueriesOf(async () =>
            (await Request(null).RequestAccess(_publicDataset, AccessType.Read)).ShouldBeTrue());

        second.ShouldBe(0);
        (await Request(null).RequestAccess(_privateDataset, AccessType.Read)).ShouldBeFalse();
    }

    [Test]
    public async Task RequestAccess_DeletedUser_IsAnonymous()
    {
        var ghost = new User { Id = "ghost", UserName = "ghost" };

        (await Request(ghost).RequestAccess(_privateDataset, AccessType.Read)).ShouldBeFalse();
        (await Request(ghost).RequestAccess(_publicDataset, AccessType.Read)).ShouldBeTrue();
        (await Request(ghost).RequestAccess(_publicDataset, AccessType.Write)).ShouldBeFalse();
    }

    [Test]
    public async Task Snapshot_IsSharedAcrossAccessChecksOfOneRequest()
    {
        var request = Request(_member);

        var queries = await QueriesOf(async () =>
        {
            await request.RequestAccess(_privateDataset, AccessType.Read);
            await request.GetDatasetPermissions(_privateDataset);
            await request.IsUserAdmin();
            await request.IsOwnerOrAdmin(_privateDataset);
        });

        queries.ShouldBe(3);
    }

    [Test]
    public async Task Invalidate_RoleChangeIsSeenByTheNextRequest()
    {
        (await Request(_member).IsUserAdmin()).ShouldBeFalse();

        _roles.Add((_member.Id, ApplicationDbContext.AdminRoleName));
        (await Request(_member).IsUserAdmin()).ShouldBeFalse("cached until invalidated or expired");

        _snapshots.Invalidate(_member.Id);
        (await Request(_member).IsUserAdmin()).ShouldBeTrue();
    }

    [Test]
    public async Task Expiration_BoundsChangesMadeElsewhere()
    {
        var time = new FakeTime(DateTime.UtcNow);
        _snapshots = new UserAuthSnapshotCache(new MemoryCache(new MemoryCacheOptions { Clock = time.SystemClock }),
            TimeSpan.FromMilliseconds(200));
        (await Request(_member).RequestAccess(_privateDataset, AccessType.Read)).ShouldBeTrue();

        // Deactivated by another instance: no invalidation reaches this one
        _roles.Add((_member.Id, ApplicationDbContext.DeactivatedRoleName));
        time.Advance(TimeSpan.FromMilliseconds(199));
        (await Request(_member).RequestAccess(_privateDataset, AccessType.Read)).ShouldBeTrue("cached until it expires");

        time.Advance(TimeSpan.FromMilliseconds(201));
        (await Request(_member).RequestAccess(_privateDataset, AccessType.Read)).ShouldBeFalse();
    }

    [Test]
    public async Task ZeroExpiration_DisablesTheCache()
    {
        _snapshots = new UserAuthSnapshotCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.Zero);
        await Request(_member).RequestAccess(_privateDataset, AccessType.Read);

        var second = await QueriesOf(() => Request(_member).RequestAccess(_privateDataset, AccessType.Read));

        second.ShouldBe(3);
    }

    [Test]
    public async Task LoadRacingAnInvalidation_DoesNotStoreTheOldRoles()
    {
        var loadStarted = new TaskCompletionSource();
        var releaseLoad = new TaskCompletionSource();
        var stale = new UserAuthSnapshot(true, "member", false, false);
        var fresh = new UserAuthSnapshot(true, "member", false, true);

        var racing = _snapshots.GetOrLoadAsync(_member.Id, async () =>
        {
            loadStarted.SetResult();
            await releaseLoad.Task;
            return stale; // read before the role change
        });
        await loadStarted.Task;

        _snapshots.Invalidate(_member.Id); // role change committed meanwhile
        releaseLoad.SetResult();
        (await racing).ShouldBe(stale);

        var next = await _snapshots.GetOrLoadAsync(_member.Id, () => Task.FromResult(fresh));
        next.ShouldBe(fresh);
    }

    [Test]
    public async Task RegistryUserManager_RoleChangesAndDeletionInvalidate()
    {
        var store = new Mock<IUserRoleStore<User>>();
        var storeRoles = new HashSet<string>();
        store.Setup(s => s.FindByIdAsync(_member.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_member);
        store.Setup(s => s.IsInRoleAsync(_member, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((User _, string role, CancellationToken _) => storeRoles.Contains(role));
        store.Setup(s => s.AddToRoleAsync(_member, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((User _, string role, CancellationToken _) => storeRoles.Add(role))
            .Returns(Task.CompletedTask);
        store.Setup(s => s.RemoveFromRoleAsync(_member, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((User _, string role, CancellationToken _) => storeRoles.Remove(role))
            .Returns(Task.CompletedTask);
        store.Setup(s => s.UpdateAsync(_member, It.IsAny<CancellationToken>())).ReturnsAsync(IdentityResult.Success);
        store.Setup(s => s.DeleteAsync(_member, It.IsAny<CancellationToken>())).ReturnsAsync(IdentityResult.Success);

        var manager = new RegistryUserManager(store.Object, Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
            new PasswordHasher<User>(), [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(),
            null!, NullLogger<UserManager<User>>.Instance, _snapshots);
        var provider = new UserAuthSnapshotProvider(manager, _snapshots);
        var admin = new UpperInvariantLookupNormalizer().NormalizeName(ApplicationDbContext.AdminRoleName);
        var deactivated = new UpperInvariantLookupNormalizer().NormalizeName(ApplicationDbContext.DeactivatedRoleName);

        (await provider.GetAsync(_member.Id)).IsAdmin.ShouldBeFalse();

        await manager.AddToRoleAsync(_member, ApplicationDbContext.AdminRoleName);
        storeRoles.ShouldContain(admin);
        (await provider.GetAsync(_member.Id)).IsAdmin.ShouldBeTrue();

        await manager.AddToRolesAsync(_member, [ApplicationDbContext.DeactivatedRoleName]);
        (await provider.GetAsync(_member.Id)).IsDeactivated.ShouldBeTrue();

        await manager.RemoveFromRolesAsync(_member, [ApplicationDbContext.DeactivatedRoleName]);
        storeRoles.ShouldNotContain(deactivated);
        (await provider.GetAsync(_member.Id)).IsDeactivated.ShouldBeFalse();

        await manager.RemoveFromRoleAsync(_member, ApplicationDbContext.AdminRoleName);
        (await provider.GetAsync(_member.Id)).IsAdmin.ShouldBeFalse();

        store.Setup(s => s.FindByIdAsync(_member.Id, It.IsAny<CancellationToken>())).ReturnsAsync((User?)null);
        await manager.DeleteAsync(_member);
        (await provider.GetAsync(_member.Id)).Exists.ShouldBeFalse();
    }

    [Test]
    public async Task UserEnrichmentMiddleware_UsesTheSnapshot()
    {
        var provider = new UserAuthSnapshotProvider(_users.Object, _snapshots);
        await provider.GetAsync(_member.Id);
        var middleware = new UserEnrichmentMiddleware(provider);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, _member.Id)]))
        };
        var called = false;

        var queries = await QueriesOf(() => middleware.InvokeAsync(context, _ =>
        {
            called = true;
            return Task.CompletedTask;
        }));

        called.ShouldBeTrue();
        queries.ShouldBe(0);
    }
}
