using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Datasets;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Services.Stac;

namespace Registry.Web.Test.Support;

/// <summary>
/// Two organizations, one private and one public, each with a public, an unlisted and a private dataset
/// ("a-public", "b-unlisted", "c-private"), and the people who look at them: the owner of both, a read-only member
/// of both, a signed-in outsider and an administrator. The services built here run on the real access rules
/// (<see cref="AuthManager"/>); only the cache, the identity store and the native indexes are stand-ins.
/// </summary>
public sealed class DatasetVisibilityWorld : IDisposable
{
    public const string PrivateOrg = "private-org";
    public const string PublicOrg = "public-org";

    public RegistryContext Context { get; }
    public Mock<IDdbManager> DdbManager { get; } = new();
    public Mock<ICacheManager> Cache { get; } = new();

    public Dictionary<Guid, Visibility> VisibilityOf { get; } = new();
    public Dictionary<Guid, string> Names { get; } = new();
    public Dictionary<Guid, Exception> NameFailures { get; } = new();

    /// <summary>The internal reference of each dataset, by "org/dataset".</summary>
    public Dictionary<string, Guid> Refs { get; } = new();

    public User Owner { get; } = new() { Id = "owner-id", UserName = "owner" };
    public User Member { get; } = new() { Id = "member-id", UserName = "member" };
    public User Outsider { get; } = new() { Id = "outsider-id", UserName = "outsider" };
    public User Administrator { get; } = new() { Id = "admin-id", UserName = "administrator" };

    private readonly Mock<UserManager<User>> _users;
    private readonly IDistributedCache _store =
        new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));

    public DatasetVisibilityWorld()
    {
        Context = TestDb.CreateContext<RegistryContext>();

        foreach (var (slug, isPublic) in new[] { (PrivateOrg, false), (PublicOrg, true) })
        {
            var org = new Organization
            {
                Slug = slug, Name = slug, OwnerId = Owner.Id, CreationDate = DateTime.UtcNow, IsPublic = isPublic,
                Users = [new OrganizationUser
                {
                    OrganizationSlug = slug, UserId = Member.Id, Permissions = OrganizationPermissions.ReadOnly,
                    GrantedAt = DateTime.UtcNow, GrantedBy = Owner.Id
                }],
                Datasets = []
            };
            foreach (var (dsSlug, visibility) in new[]
                     {
                         ("c-private", Visibility.Private), ("a-public", Visibility.Public),
                         ("b-unlisted", Visibility.Unlisted)
                     })
            {
                var ds = new Dataset { Slug = dsSlug, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org };
                org.Datasets.Add(ds);
                VisibilityOf[ds.InternalRef] = visibility;
                Names[ds.InternalRef] = $"{slug} {dsSlug}";
                Refs[$"{slug}/{dsSlug}"] = ds.InternalRef;
                DdbManager.Setup(m => m.Get(slug, ds.InternalRef)).Returns(Index(slug, ds.InternalRef));
            }

            Context.Organizations.Add(org);
        }

        Context.SaveChanges();

        Cache.Setup(c => c.GetAsync(MagicStrings.DatasetVisibilityCacheSeed, It.IsAny<string>(), It.IsAny<object[]>()))
            .ReturnsAsync((string _, string _, object[] args) => BitConverter.GetBytes((int)VisibilityOf[(Guid)args[1]]));
        Cache.Setup(c => c.GetAsync(MagicStrings.DatasetNameCacheSeed, It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<object[]>()))
            .ReturnsAsync((string _, string _, CancellationToken _, object[] args) =>
                NameFailures.TryGetValue((Guid)args[1], out var failure)
                    ? throw failure
                    : System.Text.Encoding.UTF8.GetBytes(Names[(Guid)args[1]]));

        _users = new Mock<UserManager<User>>(new Mock<IUserStore<User>>().Object, null!, null!, null!, null!, null!, null!, null!, null!);
        var everyone = new[] { Owner, Member, Outsider, Administrator };
        _users.Setup(u => u.FindByIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string id) => everyone.FirstOrDefault(u => u.Id == id));
        _users.Setup(u => u.IsInRoleAsync(Administrator, ApplicationDbContext.AdminRoleName)).ReturnsAsync(true);
    }

    // The index of a dataset: one STAC item, a photo, whose collection the answer names
    private static IDDB Index(string orgSlug, Guid internalRef)
    {
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.DatasetFolderPath).Returns($"/datasets/{orgSlug}/{internalRef}");
        ddb.Setup(d => d.GetStacData(null)).Returns(() => TestJson.Parse("""{"type":"Collection"}"""));
        ddb.Setup(d => d.GetStacItemCollectionData(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<int>()))
            .Returns(() => TestJson.Parse(
                """{"type":"FeatureCollection","numberMatched":1,"features":[{"type":"Feature","properties":{"title":"photo.jpg"},"assets":{}}]}"""));
        return ddb.Object;
    }

    /// <summary>The access rules for <paramref name="caller"/> (null: anonymous).</summary>
    public AuthManager Auth(User? caller)
    {
        var http = new Mock<IHttpContextAccessor>();
        http.Setup(h => h.HttpContext).Returns(caller == null
            ? null
            : new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, caller.Id)])) });

        // Rules only: no snapshot reuse between checks
        return new AuthManager(_users.Object, http.Object, DdbManager.Object, Context,
            NullLogger<AuthManager>.Instance, Cache.Object,
            new UserAuthSnapshotProvider(_users.Object, new UserAuthSnapshotCache(new MemoryCache(new MemoryCacheOptions()), TimeSpan.Zero)));
    }

    public DatasetAccessResolver Access(AuthManager auth) =>
        new(TestPorts.Datasets(Context), auth, DdbManager.Object, TestPaths.Policy);

    /// <summary>The dataset queries (the list of the datasets of an organization) for <paramref name="caller"/>.</summary>
    public DatasetQueries Datasets(User? caller)
    {
        var auth = Auth(caller);
        var summaries = new Mock<IDatasetSummaryProvider>();
        summaries.Setup(s => s.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(new DatasetSummary(0, null, null));
        return new DatasetQueries(Access(auth), auth, Context, summaries.Object, DdbManager.Object,
            Mock.Of<IUrlBuilder>(), Microsoft.Extensions.Options.Options.Create(new ThumbnailOptions()));
    }

    /// <summary>The STAC catalog for <paramref name="caller"/>; the listings it caches are kept per caller.</summary>
    public StacManager Stac(User? caller)
    {
        var auth = Auth(caller);
        var urls = new Mock<IUrlBuilder>();
        urls.Setup(o => o.GetLocalHost(It.IsAny<bool>())).Returns("http://localhost");
        var catalog = new StacCatalog(Context, auth, Cache.Object, DdbManager.Object, TestCaches.Records(_store),
            TestCaches.Generations(_store));
        return new StacManager(Access(auth), DdbManager.Object, catalog, new StacUrls(urls.Object),
            NullLogger<StacManager>.Instance);
    }

    public void Dispose() => Context.Dispose();
}
