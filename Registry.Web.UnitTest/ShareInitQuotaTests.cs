using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Objects;
using Registry.Web.Services.Ports;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// <c>POST …/share/init</c> and the quota (FR-XC-26, D-039): the share goes into an organization, and the quota is the one of
/// the owner of that organization, whoever starts it; an owner who is over quota is refused (507) before a dataset is created.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ShareInitQuotaTests"
/// </summary>
[TestFixture]
public class ShareInitQuotaTests
{
    private TestDb<RegistryContext> _db = null!;
    private RegistryContext _context = null!;
    private Mock<IStorageQuota> _quota = null!;
    private Mock<IDatasetsManager> _datasets = null!;
    private Mock<IAuthManager> _auth = null!;
    private ShareManager _manager = null!;

    [SetUp]
    public void SetUp()
    {
        _db = TestDb.Create<RegistryContext>();
        _context = _db.NewContext();
        _context.Organizations.Add(new Organization { Slug = "bobs", Name = "bobs", OwnerId = "bob-id", CreationDate = DateTime.UtcNow });
        _context.Organizations.Add(new Organization { Slug = "alice", Name = "alice", OwnerId = "alice-id", CreationDate = DateTime.UtcNow });
        // An organization without an owner: no quota applies to it, as on every other path
        _context.Organizations.Add(new Organization { Slug = "ownerless", Name = "ownerless", OwnerId = null, CreationDate = DateTime.UtcNow });
        _context.Datasets.Add(new Dataset
        {
            Slug = "existing", Organization = _context.Organizations.Local.Single(o => o.Slug == "bobs"),
            InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow
        });
        _context.SaveChanges();

        _quota = new Mock<IStorageQuota>();
        _quota.Setup(q => q.OwnerIdOfAsync("bobs", It.IsAny<CancellationToken>())).ReturnsAsync("bob-id");

        // Alice is a member of "bobs": she starts the share
        var auth = _auth = new Mock<IAuthManager>();
        auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { Id = "alice-id", UserName = "alice" });
        auth.Setup(a => a.SafeGetCurrentUserName()).ReturnsAsync("alice");
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
        auth.Setup(a => a.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>())).ReturnsAsync(true);

        _datasets = new Mock<IDatasetsManager>();
        _datasets.Setup(d => d.AddNew(It.IsAny<string>(), It.IsAny<DatasetNewDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string orgSlug, DatasetNewDto dto, CancellationToken _) =>
            {
                _context.Datasets.Add(new Dataset
                {
                    Slug = dto.Slug, Organization = _context.Organizations.Single(o => o.Slug == orgSlug),
                    InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow
                });
                _context.SaveChanges();
                return new DatasetDto { Slug = dto.Slug };
            });

        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings());
        var utils = TestPorts.All(_context, TestSettings.Options<AccountOptions>(settings),
            TestSettings.Options<HttpHostOptions>(settings), Mock.Of<IHttpContextAccessor>(), Mock.Of<IDatasetSummaryProvider>());
        var tokens = new Mock<IBatchTokenGenerator>();
        tokens.Setup(t => t.GenerateToken()).Returns(() => Guid.NewGuid().ToString("N"));
        var names = new Mock<INameGenerator>();
        names.Setup(n => n.GenerateName()).Returns(() => "gen-" + Guid.NewGuid().ToString("N")[..8]);

        _manager = TestShares.Manager(Mock.Of<IObjectsManager>(), _datasets.Object, Mock.Of<IOrganizationsManager>(),
            utils.Datasets, _quota.Object, auth.Object,
            new DatasetAccessResolver(utils.Datasets, auth.Object, Mock.Of<IDdbManager>(), TestPaths.Policy),
            tokens.Object, names.Object, _context, new FileSystem(), TestScratch.Provider, TimeProvider.System,
            Mock.Of<IEntryCommandService>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _db.Dispose();
    }

    [Test]
    public async Task Init_IntoAnOrganizationWhoseOwnerIsOverQuota_Is507_BeforeADatasetIsCreated()
    {
        _quota.Setup(q => q.CheckOwnerStorageAsync("bob-id", 0, It.IsAny<CancellationToken>()))
            .ThrowsAsync(QuotaExceededException.OwnerOverQuota(0));

        var ex = await Should.ThrowAsync<QuotaExceededException>(() =>
            _manager.Initialize(new ShareInitDto { OrgSlug = "bobs", DatasetName = "Survey" }));

        ex.StatusCode.ShouldBe(507);
        _datasets.Verify(d => d.AddNew(It.IsAny<string>(), It.IsAny<DatasetNewDto>(), It.IsAny<CancellationToken>()), Times.Never);
        _quota.Verify(q => q.CheckOwnerStorageAsync("alice-id", It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Init_WithATag_ChecksTheOwnerOfTheTaggedOrganization()
    {
        await _manager.Initialize(new ShareInitDto { Tag = "bobs/new-one" });

        _quota.Verify(q => q.CheckOwnerStorageAsync("bob-id", 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Init_WithoutAnOrganization_ChecksTheUser_WhoseDefaultOrganizationItGoesTo()
    {
        await _manager.Initialize(new ShareInitDto { DatasetName = "Mine" });

        _quota.Verify(q => q.CheckOwnerStorageAsync("alice-id", 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    // SEC-003: the storage of the owner is checked only once the user may write to the organization: an organization the
    // user may not write to answers its access error, not 507, so a private organization and the state of its owner's
    // storage are not told to a stranger
    [Test]
    public async Task Init_IntoAnOrganizationTheUserMayNotWriteTo_IsTheAccessError_Not507()
    {
        _auth.Setup(a => a.RequestAccess(It.Is<Organization>(o => o.Slug == "bobs"), It.IsAny<AccessType>())).ReturnsAsync(false);
        _quota.Setup(q => q.CheckOwnerStorageAsync(It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(QuotaExceededException.OwnerOverQuota(0));

        await Should.ThrowAsync<ForbiddenException>(() =>
            _manager.Initialize(new ShareInitDto { Tag = "bobs/new-one" }));

        _quota.Verify(q => q.CheckOwnerStorageAsync(It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        _datasets.Verify(d => d.AddNew(It.IsAny<string>(), It.IsAny<DatasetNewDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Init_IntoADatasetTheUserMayNotWriteTo_IsTheAccessError_Not507()
    {
        _auth.Setup(a => a.RequestAccess(It.Is<Dataset>(d => d.Slug == "existing"), It.IsAny<AccessType>())).ReturnsAsync(false);
        _quota.Setup(q => q.CheckOwnerStorageAsync(It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(QuotaExceededException.OwnerOverQuota(0));

        await Should.ThrowAsync<ForbiddenException>(() =>
            _manager.Initialize(new ShareInitDto { Tag = "bobs/existing" }));

        _quota.Verify(q => q.CheckOwnerStorageAsync(It.IsAny<string?>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Init_IntoAnExistingDatasetWhoseOwnerIsOverQuota_Is507()
    {
        _quota.Setup(q => q.CheckOwnerStorageAsync("bob-id", 0, It.IsAny<CancellationToken>()))
            .ThrowsAsync(QuotaExceededException.OwnerOverQuota(0));

        var ex = await Should.ThrowAsync<QuotaExceededException>(() =>
            _manager.Initialize(new ShareInitDto { Tag = "bobs/existing" }));

        ex.StatusCode.ShouldBe(507);
    }

    // An organization that exists without an owner has no limit (null owner), as on every other path: the quota of the
    // caller does not apply to it
    [Test]
    public async Task Init_IntoAnOrganizationWithoutAnOwner_IsNotCheckedAgainstTheCaller()
    {
        await _manager.Initialize(new ShareInitDto { OrgSlug = "ownerless", DatasetName = "Free" });

        _quota.Verify(q => q.CheckOwnerStorageAsync(null, 0, It.IsAny<CancellationToken>()), Times.Once);
        _quota.Verify(q => q.CheckOwnerStorageAsync("alice-id", It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
