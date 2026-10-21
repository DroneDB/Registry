#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Common;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Shouldly;
using Registry.Web.Test.Support;
using Registry.Test.Common;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-01 item 2 (<c>ShareManager</c>):
/// <list type="bullet">
/// <item><c>Initialize</c> into an existing dataset needs write access to it before it rolls back the
/// dataset's running batches (it used to roll back another user's batch with no check).</item>
/// <item>A rollback checks the access it needs (write to delete the entries, delete to drop the dataset)
/// before it commits <c>Status=Rolledback</c>: a denied rollback leaves the batch running.</item>
/// <item>Datasets are looked up by organization and slug: a same-slug dataset in another organization is
/// never picked.</item>
/// </list>
/// Real <see cref="WebUtils"/> on an in-memory context; access answered by a mocked <see cref="IAuthManager"/>
/// with the rules of <c>DatasetAccessControl</c> for the configured roles.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~Rq01ShareAccessTests"
/// </summary>
[TestFixture]
[Category(TestCategories.Baseline)]
public class Rq01ShareAccessTests
{
    private const string AliceId = "alice-id";
    private const string BobId = "bob-id";
    private const string AliceOrg = "alice";
    private const string BobOrg = "bob";
    private const string SharedSlug = "shared";
    private const string AliceToken = "alice-running-batch";

    private RegistryContext _context = null!;
    private Mock<IAuthManager> _auth = null!;
    private Mock<IDatasetsManager> _datasets = null!;
    private Mock<IObjectsManager> _objects = null!;
    private ShareManager _manager = null!;

    // Bob's permissions on Alice's organization (null: not a member)
    private OrganizationPermissions? _bobOnAliceOrg;

    [SetUp]
    public void SetUp()
    {
        _context = TestDb.CreateContext<RegistryContext>();

        var aliceOrg = new Organization
            { Slug = AliceOrg, Name = AliceOrg, OwnerId = AliceId, CreationDate = DateTime.UtcNow };
        var bobOrg = new Organization
            { Slug = BobOrg, Name = BobOrg, OwnerId = BobId, CreationDate = DateTime.UtcNow };
        var aliceDataset = new Dataset
        {
            Slug = SharedSlug, Organization = aliceOrg, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow
        };
        _context.Organizations.AddRange(aliceOrg, bobOrg);
        _context.Datasets.Add(aliceDataset);
        _context.Batches.Add(new Batch
        {
            Token = AliceToken, Dataset = aliceDataset, UserName = "alice", Start = DateTime.UtcNow,
            Status = BatchStatus.Running
        });
        _context.SaveChanges();

        // Current user: Bob, not an admin
        _bobOnAliceOrg = null;
        _auth = new Mock<IAuthManager>();
        _auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { Id = BobId, UserName = "bob" });
        _auth.Setup(a => a.SafeGetCurrentUserName()).ReturnsAsync("bob");
        _auth.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        _auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>()))
            .ReturnsAsync((Dataset ds, AccessType access) => BobCan(ds.Organization, access));
        _auth.Setup(a => a.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>()))
            .ReturnsAsync((Organization org, AccessType access) => BobCan(org, access));

        // The datasets manager adds the dataset where asked, and deletes it only with delete access, as the
        // real one does
        _datasets = new Mock<IDatasetsManager>();
        _datasets.Setup(d => d.AddNew(It.IsAny<string>(), It.IsAny<DatasetNewDto>()))
            .ReturnsAsync((string orgSlug, DatasetNewDto dto) =>
            {
                _context.Datasets.Add(new Dataset
                {
                    Slug = dto.Slug, Organization = _context.Organizations.Single(o => o.Slug == orgSlug),
                    InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow
                });
                _context.SaveChanges();
                return new DatasetDto { Slug = dto.Slug };
            });
        _datasets.Setup(d => d.Delete(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(async (string orgSlug, string dsSlug) =>
            {
                var ds = _context.Datasets.Include(x => x.Organization)
                    .Single(x => x.Slug == dsSlug && x.Organization.Slug == orgSlug);
                if (!await _auth.Object.RequestAccess(ds, AccessType.Delete))
                    throw new UnauthorizedException("The current user cannot delete this dataset");
            });
        _objects = new Mock<IObjectsManager>();

        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings());
        var utils = new WebUtils(_auth.Object, _context, settings, Mock.Of<IHttpContextAccessor>(),
            Mock.Of<IDatasetSummaryProvider>());

        var tokens = new Mock<IBatchTokenGenerator>();
        tokens.Setup(t => t.GenerateToken()).Returns(() => Guid.NewGuid().ToString("N"));
        var names = new Mock<INameGenerator>();
        names.Setup(n => n.GenerateName()).Returns(() => "gen-" + Guid.NewGuid().ToString("N")[..8]);

        _manager = new ShareManager(settings, NullLogger<ShareManager>.Instance, _objects.Object,
            _datasets.Object, Mock.Of<IOrganizationsManager>(), utils, _auth.Object,
            new DatasetAccessResolver(utils, _auth.Object, Mock.Of<IDdbManager>()), tokens.Object,
            names.Object, _context, new FileSystem());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    // DatasetAccessControl for a non-admin: the owner can do anything, a member what its permission allows
    private bool BobCan(Organization org, AccessType access) =>
        org.OwnerId == BobId || (org.Slug == AliceOrg && _bobOnAliceOrg?.HasAccess(access) == true);

    private BatchStatus AliceBatchStatus() =>
        _context.Batches.AsNoTracking().Single(b => b.Token == AliceToken).Status;

    private Batch BatchOf(string token) =>
        _context.Batches.AsNoTracking().Include(b => b.Dataset.Organization).Single(b => b.Token == token);

    private void BobOwnsTheRunningBatch()
    {
        var batch = _context.Batches.Single(b => b.Token == AliceToken);
        batch.UserName = "bob";
        _context.SaveChanges();
    }

    [TestCase(null)]
    [TestCase(OrganizationPermissions.ReadOnly)]
    public async Task Initialize_ExistingDatasetWithoutWriteAccess_IsDeniedAndKeepsTheRunningBatch(
        OrganizationPermissions? bobPermissions)
    {
        _bobOnAliceOrg = bobPermissions;

        await Should.ThrowAsync<ForbiddenException>(() =>
            _manager.Initialize(new ShareInitDto { Tag = $"{AliceOrg}/{SharedSlug}" }));

        AliceBatchStatus().ShouldBe(BatchStatus.Running);
        _context.Batches.Count().ShouldBe(1, "no batch may be created for a denied user");
    }

    [Test]
    public async Task Initialize_ExistingDatasetWithWriteAccess_SupersedesTheRunningBatch()
    {
        // Unchanged semantics: a writer starting a new batch replaces the running one
        _bobOnAliceOrg = OrganizationPermissions.ReadWrite;

        var result = await _manager.Initialize(new ShareInitDto { Tag = $"{AliceOrg}/{SharedSlug}" });

        AliceBatchStatus().ShouldBe(BatchStatus.Rolledback);
        var batch = BatchOf(result.Token);
        batch.Dataset.Organization.Slug.ShouldBe(AliceOrg);
        batch.Status.ShouldBe(BatchStatus.Running);
    }

    [Test]
    public async Task Initialize_NewDatasetWithSlugTakenInAnotherOrg_UsesTheRequestedOrg()
    {
        // "shared" exists in Alice's organization only: Bob's new "bob/shared" must not resolve to it
        var result = await _manager.Initialize(new ShareInitDto { Tag = $"{BobOrg}/{SharedSlug}" });

        var batch = BatchOf(result.Token);
        batch.Dataset.Organization.Slug.ShouldBe(BobOrg);
        batch.Dataset.Slug.ShouldBe(SharedSlug);
        AliceBatchStatus().ShouldBe(BatchStatus.Running);
    }

    [Test]
    public async Task Rollback_AnotherUsersBatch_IsDeniedAndKeepsItRunning()
    {
        _bobOnAliceOrg = OrganizationPermissions.ReadWriteDelete;

        await Should.ThrowAsync<BadRequestException>(() => _manager.Rollback(AliceToken));

        AliceBatchStatus().ShouldBe(BatchStatus.Running);
        _datasets.Verify(d => d.Delete(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task Rollback_OwnBatchWithoutDeleteAccess_IsDeniedBeforeTheStatusChanges()
    {
        // Bob may write (so he could start the batch) but not delete the dataset the rollback drops
        _bobOnAliceOrg = OrganizationPermissions.ReadWrite;
        BobOwnsTheRunningBatch();

        await Should.ThrowAsync<ForbiddenException>(() => _manager.Rollback(AliceToken));

        AliceBatchStatus().ShouldBe(BatchStatus.Running);
        BatchOf(AliceToken).End.ShouldBeNull();
    }

    [Test]
    public async Task Rollback_OwnBatchWithDeleteAccess_RollsBack()
    {
        _bobOnAliceOrg = OrganizationPermissions.ReadWriteDelete;
        BobOwnsTheRunningBatch();

        await _manager.Rollback(AliceToken);

        AliceBatchStatus().ShouldBe(BatchStatus.Rolledback);
        _datasets.Verify(d => d.Delete(AliceOrg, SharedSlug), Times.Once);
    }
}
