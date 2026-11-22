using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Shouldly;

namespace Registry.Web.Test;

/// <summary>
/// The stored shape of the report settings: the version is a concurrency token (a write that read an older version does
/// not overwrite a newer one, whatever the manager checked before), and the row goes with its organization.
/// </summary>
[TestFixture]
public class OrganizationReportSettingsModelTests
{
    private SqliteConnection _connection = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var context = Create();
        context.Database.EnsureCreated();
        context.Organizations.Add(new Organization { Slug = "acme", Name = "Acme", CreationDate = DateTime.UtcNow });
        context.OrganizationReportSettings.Add(Row("acme", 1));
        context.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private RegistryContext Create() =>
        new(new DbContextOptionsBuilder<RegistryContext>().UseSqlite(_connection).Options);

    private static OrganizationReportSettings Row(string org, int version) => new()
    {
        OrganizationSlug = org, Templates = "[]", Schema = "{}", Version = version, UpdatedAt = DateTime.UtcNow, UpdatedBy = "u"
    };

    [Test]
    public void AWriterThatReadAnOlderVersion_DoesNotOverwriteANewerOne()
    {
        using var first = Create();
        using var second = Create();
        var read1 = first.OrganizationReportSettings.Single(r => r.OrganizationSlug == "acme");
        var read2 = second.OrganizationReportSettings.Single(r => r.OrganizationSlug == "acme");

        read1.Version = 2;
        read1.Templates = "[1]";
        first.SaveChanges();

        read2.Version = 2;
        read2.Templates = "[2]";
        Should.Throw<DbUpdateConcurrencyException>(() => second.SaveChanges());

        using var check = Create();
        check.OrganizationReportSettings.Single().Templates.ShouldBe("[1]");
    }

    [Test]
    public void TheRow_GoesWithItsOrganization()
    {
        using var context = Create();
        context.Organizations.Remove(context.Organizations.Single());
        context.SaveChanges();

        context.OrganizationReportSettings.Count().ShouldBe(0);
    }

    [Test]
    public void ASecondRowOfAnOrganization_IsRefused()
    {
        using var context = Create();
        context.OrganizationReportSettings.Add(Row("acme", 1));

        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }

    [Test]
    public void ARowOfNoOrganization_IsRefused()
    {
        using var context = Create();
        context.OrganizationReportSettings.Add(Row("ghost", 1));

        Should.Throw<DbUpdateException>(() => context.SaveChanges());
    }
}
