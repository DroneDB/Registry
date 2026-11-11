using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Ports.DroneDB;
using Registry.Web.Data.Models;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using MetaManager = Registry.Web.Services.Managers.MetaManager;
using IMetaManager = Registry.Ports.DroneDB.IMetaManager;

namespace Registry.Web.Test;

/// <summary>
/// The meta API hands every set and unset to <see cref="IDatasetListingInvalidator.MetaChangedAsync"/>, which holds
/// the rule of which key changes a cache (FR-DEV-03): MetaManager no longer tests the key itself.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~MetaManagerListingTests"
/// </summary>
[TestFixture]
public class MetaManagerListingTests
{
    private readonly Dataset _ds = new()
    {
        Slug = "ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow,
        Organization = new Organization { Slug = "org", Name = "Org", OwnerId = "u", CreationDate = DateTime.UtcNow }
    };

    private Mock<IMetaManager> _meta = null!;
    private Mock<IDatasetListingInvalidator> _listings = null!;
    private MetaManager _manager = null!;

    [SetUp]
    public void Setup()
    {
        _meta = new Mock<IMetaManager>();
        _meta.Setup(m => m.Set(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(new Meta { Id = "m1" });
        _meta.Setup(m => m.Unset(It.IsAny<string>(), It.IsAny<string?>())).Returns(1);
        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.Meta).Returns(_meta.Object);
        ddb.Setup(d => d.EntryExists(It.IsAny<string>())).Returns(true);

        var access = new Mock<IDatasetAccessResolver>();
        access.Setup(a => a.ResolveAsync("org", "ds", AccessType.Write, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DatasetAccess(_ds, () => ddb.Object));

        _listings = new Mock<IDatasetListingInvalidator>();
        _manager = new MetaManager(NullLogger<MetaManager>.Instance, access.Object, _listings.Object);
    }

    [TestCase("visibility", null)]
    [TestCase("name", null)]
    [TestCase("name", "photos/a.jpg")]
    [TestCase("description", null)]
    public async Task Set_HandsTheKeyToTheInvalidator(string key, string? path)
    {
        await _manager.Set("org", "ds", key, "0", path);

        _listings.Verify(l => l.MetaChangedAsync("org", _ds.InternalRef, key, path, It.IsAny<CancellationToken>()),
            Times.Once);
        _listings.VerifyNoOtherCalls();
    }

    [TestCase("visibility", null)]
    [TestCase("name", null)]
    public async Task Unset_HandsTheKeyToTheInvalidator(string key, string? path)
    {
        await _manager.Unset("org", "ds", key, path);

        _listings.Verify(l => l.MetaChangedAsync("org", _ds.InternalRef, key, path, It.IsAny<CancellationToken>()),
            Times.Once);
        _listings.VerifyNoOtherCalls();
    }
}
