using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
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
/// <c>POST …/share/init</c> (§7.1, FR-UPL-02, D-009): the dataset a request creates has the visibility it sends, Public
/// when it sends none (<c>ddb share</c>, older clients), and a slug made from its name (<c>-n</c> when taken). The
/// rollback of a batch deletes the dataset only when the batch created it (FR-UPL-01).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~ShareInitVisibilityTests"
/// </summary>
[TestFixture]
public class ShareInitVisibilityTests
{
    private const string Org = "alice";
    private const string OtherOrg = "bob";

    private TestDb<RegistryContext> _db = null!;
    private RegistryContext _context = null!;
    private Mock<IDatasetsManager> _datasets = null!;
    private ShareManager _manager = null!;
    private readonly List<DatasetNewDto> _created = [];
    private readonly List<(string Org, string Ds)> _deleted = [];

    [SetUp]
    public void SetUp()
    {
        _db = TestDb.Create<RegistryContext>();
        _context = _db.NewContext();
        _created.Clear();
        _deleted.Clear();

        var alice = new Organization { Slug = Org, Name = Org, OwnerId = "alice-id", CreationDate = DateTime.UtcNow };
        var bob = new Organization { Slug = OtherOrg, Name = OtherOrg, OwnerId = "bob-id", CreationDate = DateTime.UtcNow };
        _context.Organizations.AddRange(alice, bob);
        _context.Datasets.Add(new Dataset
            { Slug = "existing", Organization = alice, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow });
        _context.SaveChanges();

        var auth = new Mock<IAuthManager>();
        auth.Setup(a => a.GetCurrentUser()).ReturnsAsync(new User { Id = "alice-id", UserName = "alice" });
        auth.Setup(a => a.SafeGetCurrentUserName()).ReturnsAsync("alice");
        auth.Setup(a => a.IsUserAdmin()).ReturnsAsync(false);
        auth.Setup(a => a.RequestAccess(It.IsAny<Dataset>(), It.IsAny<AccessType>())).ReturnsAsync(true);
        auth.Setup(a => a.RequestAccess(It.IsAny<Organization>(), It.IsAny<AccessType>())).ReturnsAsync(true);

        _datasets = new Mock<IDatasetsManager>();
        _datasets.Setup(d => d.AddNew(It.IsAny<string>(), It.IsAny<DatasetNewDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string orgSlug, DatasetNewDto dto, CancellationToken _) =>
            {
                _created.Add(dto);
                _context.Datasets.Add(new Dataset
                {
                    Slug = dto.Slug, Organization = _context.Organizations.Single(o => o.Slug == orgSlug),
                    InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow
                });
                _context.SaveChanges();
                return new DatasetDto { Slug = dto.Slug };
            });
        _datasets.Setup(d => d.Delete(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string orgSlug, string dsSlug, CancellationToken _) =>
            {
                _deleted.Add((orgSlug, dsSlug));
                return Task.CompletedTask;
            });

        var settings = Microsoft.Extensions.Options.Options.Create(new AppSettings());
        var utils = TestPorts.All(auth.Object, _context, TestSettings.Options<AccountOptions>(settings),
            TestSettings.Options<HttpHostOptions>(settings), Mock.Of<IHttpContextAccessor>(), Mock.Of<IDatasetSummaryProvider>());
        var tokens = new Mock<IBatchTokenGenerator>();
        tokens.Setup(t => t.GenerateToken()).Returns(() => Guid.NewGuid().ToString("N"));
        var names = new Mock<INameGenerator>();
        names.Setup(n => n.GenerateName()).Returns(() => "gen-" + Guid.NewGuid().ToString("N")[..8]);

        _manager = TestShares.Manager(Mock.Of<IObjectsManager>(), _datasets.Object, Mock.Of<IOrganizationsManager>(),
            utils.Datasets, utils.Quota, auth.Object,
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

    private void Taken(string orgSlug, params string[] slugs)
    {
        var org = _context.Organizations.Single(o => o.Slug == orgSlug);
        foreach (var slug in slugs)
            _context.Datasets.Add(new Dataset
                { Slug = slug, Organization = org, InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow });
        _context.SaveChanges();
    }

    private Batch Batch(string token) => _context.Batches.AsNoTracking().Include(b => b.Dataset.Organization).Single(b => b.Token == token);

    [TestCase(Visibility.Private)]
    [TestCase(Visibility.Unlisted)]
    [TestCase(Visibility.Public)]
    public async Task Init_CreatesTheDatasetWithTheVisibilityOfTheRequest(Visibility visibility)
    {
        var result = await _manager.Initialize(new ShareInitDto
            { OrgSlug = Org, DatasetName = "Survey north", Visibility = visibility });

        var dto = _created.ShouldHaveSingleItem();
        dto.Visibility.ShouldBe(visibility);
        dto.Name.ShouldBe("Survey north");
        dto.Slug.ShouldBe("survey-north");
        Batch(result.Token).Dataset.Organization.Slug.ShouldBe(Org);
    }

    [Test]
    public async Task Init_WithoutAVisibility_KeepsPublic_ForDdbShareAndOlderClients()
    {
        await _manager.Initialize(new ShareInitDto { OrgSlug = Org, DatasetName = "Old client" });
        await _manager.Initialize(new ShareInitDto { Tag = $"{Org}/tagged-new" });

        _created.Select(d => d.Visibility).ShouldBe([Visibility.Public, Visibility.Public]);
    }

    [Test]
    public async Task Init_OfATaggedNewDataset_UsesTheVisibilityToo()
    {
        await _manager.Initialize(new ShareInitDto { Tag = $"{Org}/tagged-new", Visibility = Visibility.Private });

        _created.ShouldHaveSingleItem().Visibility.ShouldBe(Visibility.Private);
    }

    [Test]
    public async Task Init_WhenTheSlugOfTheNameIsTaken_AddsTheLowestFreeSuffix_OfTwoOrMore()
    {
        // "-2" is free before "-3", and the same slug in another organization does not count
        Taken(Org, "my-survey", "my-survey-3");
        Taken(OtherOrg, "my-survey-2");

        await _manager.Initialize(new ShareInitDto { OrgSlug = Org, DatasetName = "My Survey" });

        _created.ShouldHaveSingleItem().Slug.ShouldBe("my-survey-2");
    }

    [Test]
    public async Task Init_AfterTheSuffixTwo_GoesToThree()
    {
        Taken(Org, "my-survey", "my-survey-2");

        await _manager.Initialize(new ShareInitDto { OrgSlug = Org, DatasetName = "My Survey" });

        _created.ShouldHaveSingleItem().Slug.ShouldBe("my-survey-3");
    }

    [Test]
    public async Task Init_OfATakenSlugOfTheLimitLength_StaysWithinTheLimit_AndEndsWithTwo()
    {
        var longName = new string('a', 200);
        Taken(Org, new string('a', 128));

        await _manager.Initialize(new ShareInitDto { OrgSlug = Org, DatasetName = longName });

        var dto = _created.ShouldHaveSingleItem();
        dto.Slug.ShouldBe(new string('a', 126) + "-2");
        dto.Slug.Length.ShouldBe(128);
    }

    [Test]
    public async Task Init_WithoutAName_UsesARandomSlug()
    {
        await _manager.Initialize(new ShareInitDto { OrgSlug = Org });

        _created.ShouldHaveSingleItem().Slug.ShouldStartWith("gen-");
    }

    [Test]
    public async Task Init_ThatCreatesTheDataset_MarksTheBatch_AndAnExistingDatasetIsNotMarked()
    {
        var created = await _manager.Initialize(new ShareInitDto { OrgSlug = Org, DatasetName = "Fresh" });
        var existing = await _manager.Initialize(new ShareInitDto { Tag = $"{Org}/existing" });

        Batch(created.Token).CreatedDataset.ShouldBeTrue();
        Batch(existing.Token).CreatedDataset.ShouldBeFalse();
    }

    [Test]
    public async Task Rollback_OfABatchThatCreatedTheDataset_DeletesTheDataset()
    {
        var result = await _manager.Initialize(new ShareInitDto { OrgSlug = Org, DatasetName = "Fresh" });

        await _manager.Rollback(result.Token);

        _deleted.ShouldBe([(Org, "fresh")]);
        Batch(result.Token).Status.ShouldBe(BatchStatus.Rolledback);
    }

    [Test]
    public async Task Rollback_OfABatchOnAnExistingDataset_KeepsTheDataset()
    {
        // `ddb share org/existing` then a rollback used to delete the whole dataset
        var result = await _manager.Initialize(new ShareInitDto { Tag = $"{Org}/existing" });

        await _manager.Rollback(result.Token);

        _deleted.ShouldBeEmpty();
        Batch(result.Token).Status.ShouldBe(BatchStatus.Rolledback);
    }
}
