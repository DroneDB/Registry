#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity;
using Shouldly;

namespace Registry.Web.Test.Support;

[TestFixture]
public class TestDbTests
{
    private static Organization Org(string slug = "org") =>
        new() { Slug = slug, Name = slug, CreationDate = DateTime.UtcNow };

    [Test]
    public void Create_ContextsShareTheDatabase_AndDisposeDropsIt()
    {
        var db = TestDb.Create<RegistryContext>();
        using (var write = db.NewContext())
        {
            write.Organizations.Add(Org());
            write.SaveChanges();
        }

        var read = db.NewContext();
        read.Organizations.Single().Slug.ShouldBe("org");

        db.Dispose();
        Should.Throw<ObjectDisposedException>(() => read.Organizations.Count());
        Should.Throw<ObjectDisposedException>(() => db.NewContext());
    }

    [Test]
    public void Create_EnforcesForeignKeys()
    {
        using var db = TestDb.Create<RegistryContext>();
        using var context = db.NewContext();
        // The organization is attached as existing but was never inserted.
        var ghost = Org("ghost");
        context.Attach(ghost);
        context.Datasets.Add(new Dataset { Slug = "ds", InternalRef = Guid.NewGuid(), CreationDate = DateTime.UtcNow, Organization = ghost });

        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }

    [Test]
    public void CreateContext_SeedsOnAnotherContext_AndCreatesBothSchemas()
    {
        using var context = TestDb.CreateContext<RegistryContext>(seed =>
        {
            seed.Organizations.Add(Org());
            seed.SaveChanges();
        });

        context.ChangeTracker.Entries().ShouldBeEmpty();
        context.Organizations.Single().Slug.ShouldBe("org");

        using var identity = TestDb.CreateContext<ApplicationDbContext>();
        identity.Users.Count().ShouldBe(0);
    }

    [Test]
    public async Task CreateFile_UsesWal_OneConnectionPerContext_AndDeletesTheFile()
    {
        string file;
        await using (var db = TestDb.CreateFile<RegistryContext>())
        {
            await using var first = db.NewContext();
            await using var second = db.NewContext();
            file = first.Database.GetDbConnection().DataSource;
            File.Exists(file).ShouldBeTrue();
            first.Database.GetDbConnection().ShouldNotBeSameAs(second.Database.GetDbConnection());

            await first.Database.OpenConnectionAsync();
            await using (var pragma = first.Database.GetDbConnection().CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode";
                (await pragma.ExecuteScalarAsync()).ShouldBe("wal");
            }
            await first.Database.CloseConnectionAsync();

            // Concurrent writers on separate connections, as in production.
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(async () =>
            {
                await using var context = db.NewContext();
                context.Organizations.Add(Org("org-" + i));
                await context.SaveChangesAsync();
            })));
            (await second.Organizations.CountAsync()).ShouldBe(4);
        }

        File.Exists(file).ShouldBeFalse();
        File.Exists(file + "-wal").ShouldBeFalse();
    }
}
