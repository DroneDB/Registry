using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using NUnit.Framework;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Datasets;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The creation of a dataset (<see cref="DatasetCreation"/>, the user path and the system path of a task): it writes the
/// folder and the meta of the new dataset before it saves the row, so a creation that loses the race for its slug
/// (another request saved the same slug between the check and the save) deletes that folder: it belongs to no dataset.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~DatasetCreationTests"
/// </summary>
[TestFixture]
public class DatasetCreationTests
{
    private const string Org = "creation-org";

    private CompetitorInterceptor _competitor = null!;
    private TestDb<RegistryContext> _db = null!;
    private Mock<IDdbManager> _ddbManager = null!;
    private Guid? _folder;

    [SetUp]
    public void SetUp()
    {
        _competitor = new CompetitorInterceptor();
        _db = TestDb.Create<RegistryContext>(o => o.AddInterceptors(_competitor));
        using (var seed = _db.NewContext())
        {
            seed.Organizations.Add(new Organization { Slug = Org, Name = Org, CreationDate = DateTime.UtcNow });
            seed.SaveChanges();
        }

        var ddb = new Mock<IDDB>();
        ddb.SetupGet(d => d.Meta).Returns(new Mock<IMetaManager>().Object);
        ddb.Setup(d => d.GetInfo()).Returns(new Registry.Ports.DroneDB.Entry { Properties = new Dictionary<string, JsonElement>() });

        _folder = null;
        _ddbManager = new Mock<IDdbManager>();
        _ddbManager.Setup(m => m.Get(Org, It.IsAny<Guid>()))
            .Callback<string, Guid>((_, internalRef) => _folder = internalRef)
            .Returns(ddb.Object);
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private async Task<CreatedDatasetInfo> CreateAsync(string slug)
    {
        var context = _db.NewContext();
        var org = await context.Organizations.AsTracking().Include(o => o.Datasets).SingleAsync(o => o.Slug == Org);
        return await new DatasetCreation(context, _ddbManager.Object)
            .CreateAsync(org, new DatasetNewDto { Slug = slug }, ct: CancellationToken.None);
    }

    [Test]
    public async Task LosingTheRaceForTheSlug_DeletesTheFolderItWrote_AndAnswersThatTheSlugIsTaken()
    {
        var competing = Guid.NewGuid();
        _competitor.Slug = "raced";
        _competitor.Insert = () =>
        {
            using var other = _db.NewContext();
            other.Datasets.Add(new Dataset
            {
                Slug = "raced", InternalRef = competing, CreationDate = DateTime.UtcNow,
                Organization = other.Organizations.Single(o => o.Slug == Org)
            });
            other.SaveChanges();
        };

        var ex = await Should.ThrowAsync<BadRequestException>(() => CreateAsync("raced"));

        ex.Message.ShouldBe("Dataset with this slug already exists");
        _competitor.Inserted.ShouldBeTrue("the competing dataset was saved between the check and the save");
        _folder.ShouldNotBeNull();
        _folder.ShouldNotBe(competing);
        // The folder (and the meta) the losing creation wrote goes; the winner's folder is not touched
        _ddbManager.Verify(m => m.Delete(Org, _folder!.Value), Times.Once);
        _ddbManager.Verify(m => m.Delete(Org, competing), Times.Never);

        await using var check = _db.NewContext();
        check.Datasets.Where(d => d.Slug == "raced").Select(d => d.InternalRef).ToList().ShouldBe([competing]);
    }

    [Test]
    public async Task ACreationThatSavesItsSlug_KeepsItsFolder()
    {
        var created = await CreateAsync("kept");

        created.InternalRef.ShouldBe(_folder!.Value);
        _ddbManager.Verify(m => m.Delete(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
    }

    [Test]
    public async Task ExistingSlug_FindsTheDatasetByItsFolderId_InItsOrganizationOnly()
    {
        var created = await CreateAsync("by-ref");
        await using var context = _db.NewContext();
        var creation = new DatasetCreation(context, _ddbManager.Object);

        (await creation.ExistingSlugAsync(Org, created.InternalRef)).ShouldBe("by-ref");
        (await creation.ExistingSlugAsync("another-org", created.InternalRef)).ShouldBeNull();
        (await creation.ExistingSlugAsync(Org, Guid.NewGuid())).ShouldBeNull();
    }

    /// <summary>Runs <see cref="Insert"/> once, right before the first save that adds a dataset with <see cref="Slug"/>.</summary>
    private sealed class CompetitorInterceptor : SaveChangesInterceptor
    {
        public string? Slug { get; set; }

        public Action? Insert { get; set; }

        public bool Inserted { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Slug != null && Insert != null && eventData.Context is RegistryContext context &&
                context.ChangeTracker.Entries<Dataset>().Any(e => e.State == EntityState.Added && e.Entity.Slug == Slug))
            {
                Slug = null;
                Insert();
                Inserted = true;
            }

            return ValueTask.FromResult(result);
        }
    }
}
