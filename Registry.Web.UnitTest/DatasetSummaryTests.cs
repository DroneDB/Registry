#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;
using Entry = Registry.Ports.DroneDB.Entry;
using Registry.Web.Test.Support;
using Registry.Web.Services.Caching;

namespace Registry.Web.Test;

/// <summary>
/// REG-06: the storage quota and the dataset listing read dataset summaries reused while the
/// index files are unchanged (one native DDBInfo per dataset and change, not per call), and
/// the STAC root catalog takes visibility from the visibility cache, so private datasets cost
/// no native call once cached.
/// </summary>
[TestFixture]
public class DatasetSummaryTests : TestBase
{
    private const int OwnedDatasets = 200;
    private const int ChunksPerUpload = 50;

    private RegistryContext _ctx = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private readonly Dictionary<Guid, string?> _fingerprints = new();
    private int _infoCalls;
    private readonly User _user = new() { Id = "u1", UserName = "u1" };

    [SetUp]
    public void Setup()
    {
        _infoCalls = 0;
        _fingerprints.Clear();
        _ctx = TestDb.CreateContext<RegistryContext>();
        var org = new Organization
        {
            Slug = "org", Name = "Org", OwnerId = _user.Id, CreationDate = DateTime.UtcNow,
            Datasets = new List<Dataset>()
        };
        for (var i = 0; i < OwnedDatasets; i++)
        {
            var ds = new Dataset { Slug = $"ds{i}", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = org };
            org.Datasets.Add(ds);
            _fingerprints[ds.InternalRef] = "v1";
        }
        _ctx.Organizations.Add(org);
        _ctx.SaveChanges();

        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns((string _, Guid internalRef) =>
        {
            var ddb = new Mock<IDDB>();
            ddb.Setup(d => d.GetIndexFingerprint()).Returns(() => _fingerprints.GetValueOrDefault(internalRef));
            ddb.Setup(d => d.GetInfo()).Returns(() =>
            {
                _infoCalls++;
                return new Entry { Size = 1_000_000, Properties = new Dictionary<string, object> { ["entries"] = 10 } };
            });
            return ddb.Object;
        });
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private WebUtils CreateUtils(IDatasetSummaryProvider summaries)
    {
        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _user.Metadata[MagicStrings.MaxStorageKey] = 1_000_000_000L;
        auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(_user);
        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings { EnableStorageLimiter = true, DatasetsPath = "/tmp" });
        return new WebUtils(auth.Object, _ctx, TestSettings.Options<AccountOptions>(settings), TestSettings.Options<HttpHostOptions>(settings), Mock.Of<IHttpContextAccessor>(), summaries);
    }

    [Test]
    public async Task StorageQuota_ReadsEachDatasetOncePerIndexChange()
    {
        var utils = CreateUtils(TestCaches.Summaries(_ddbManager.Object));

        for (var chunk = 0; chunk < ChunksPerUpload; chunk++)
            await utils.CheckCurrentUserStorage(8 * 1024 * 1024);

        _infoCalls.ShouldBe(OwnedDatasets, "one native call per dataset, not per chunk");
        utils.GetUserStorage(_user).Used.ShouldBe(OwnedDatasets * 1_000_000L);

        // A write to one dataset changes its index fingerprint: only that one is read again
        _fingerprints[_fingerprints.Keys.First()] = "v2";
        await utils.CheckCurrentUserStorage(8 * 1024 * 1024);
        _infoCalls.ShouldBe(OwnedDatasets + 1);
    }

    [Test]
    public void Summary_WithoutFingerprint_IsNotCached()
    {
        var internalRef = _fingerprints.Keys.First();
        _fingerprints[internalRef] = null;
        var summaries = TestCaches.Summaries(_ddbManager.Object);

        summaries.Get("org", internalRef).Size.ShouldBe(1_000_000);
        summaries.Get("org", internalRef);

        _infoCalls.ShouldBe(2);
    }

    [Test]
    public async Task StacCatalog_PrivateDatasets_NoNativeCallsOnceCached()
    {
        var visibilityReads = 0;
        var meta = new Mock<Registry.Ports.DroneDB.IMetaManager>();
        meta.Setup(m => m.Get<int>(SafeMetaManager.VisibilityField, null))
            .Returns(() => { visibilityReads++; return (int)Visibility.Private; });
        var ddb = new Mock<IDDB>();
        ddb.Setup(d => d.Meta).Returns(meta.Object);
        var ddbManager = new Mock<IDdbManager>();
        ddbManager.Setup(m => m.Get(It.IsAny<string>(), It.IsAny<Guid>())).Returns(ddb.Object);

        var utils = new Mock<IUtils>();
        utils.Setup(u => u.GenerateStacUrl()).Returns("http://localhost/stac");
        var cache = new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
        var auth = Mock.Of<IAuthManager>();
        var stac = new StacManager(auth, new DatasetAccessResolver(utils.Object, auth, ddbManager.Object, TestPaths.Policy), _ctx, utils.Object,
            ddbManager.Object, TestCaches.Records(cache),
            CreateTestLogger<StacManager>(), TestCaches.WithDatasetVisibility(cache));

        (await stac.GetCatalog()).Links.ShouldNotContain(l => l.Relationship == "child");
        visibilityReads.ShouldBe(OwnedDatasets);

        await stac.GetCatalog();
        visibilityReads.ShouldBe(OwnedDatasets, "the second request reads no index");
    }
}
