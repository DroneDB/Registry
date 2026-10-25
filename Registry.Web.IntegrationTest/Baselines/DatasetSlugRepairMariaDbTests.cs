#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Registry.Adapters;
using Registry.Ports;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Maintenance;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The slug repair (<c>POST /sys/fix-invalid-slugs</c>, <see cref="DatasetSlugRepair"/>) on the MariaDB
/// production runs (<see cref="MariaDbServer"/>: the retrying execution strategy of MySQL, the case-insensitive
/// <c>latin1_general_ci</c> collation, the real foreign keys), with the <c>JobIndices</c> rows that name the repaired
/// datasets by slug: the history and progress of a job must follow its dataset to the new slug, in the transaction that
/// renames it. The same scenarios run on SQLite in <c>DatasetSlugRepairTest</c> (Registry.Web.UnitTest).
/// Run: dotnet test Registry.Web.IntegrationTest --filter "FullyQualifiedName~DatasetSlugRepairMariaDbTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
[Category(TestCategories.Network)]
public class DatasetSlugRepairMariaDbTests
{
    private string _datasetsPath = null!;

    [SetUp]
    public void SetUp()
    {
        _datasetsPath = Path.Combine(Path.GetTempPath(), "fix-slugs-mariadb-" + Guid.NewGuid());
        Directory.CreateDirectory(_datasetsPath);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_datasetsPath))
            Directory.Delete(_datasetsPath, true);
    }

    [Test]
    public async Task DatasetAndOrganizationRename_RewritesTheJobsOfTheRepairedDatasetsOnly()
    {
        var server = await MariaDbServer.GetAsync();
        await using var database = await server.CreateDatabaseAsync("datasetslugrepairmariadb_slugrepair");
        await using var services = HostDbContexts.Build<RegistryContext>(DbProvider.Mysql, database.ConnectionString);
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RegistryContext>();
        await context.GetService<IMigrator>().MigrateAsync();
        await SeedAsync(context);

        var result = await new DatasetSlugRepair(context, new Mock<IDdbManager>().Object, new FileSystem(),
            Microsoft.Extensions.Options.Options.Create(new StorageOptions { DatasetsPath = _datasetsPath }),
            new Mock<IDatasetListingInvalidator>().Object, NullLogger<DatasetSlugRepair>.Instance)
            .FixInvalidSlugsAsync(dryRun: false);

        result.Entries.ShouldAllBe(e => e.Status == "renamed", string.Join("; ", result.Entries.Select(e => e.Reason)));
        await using var check = services.CreateAsyncScope();
        var after = check.ServiceProvider.GetRequiredService<RegistryContext>();

        // The organization "Plot" is "plot" (a case-only rename: one key for this collation), "a--b" is "a-b"
        after.Organizations.Select(o => o.Slug).ToArray().ShouldBe(["a-b", "plot", "stable"], ignoreOrder: true);
        after.Datasets.Select(d => d.Slug).ToArray().ShouldAllBe(s => SlugRules.IsValid(s));

        Jobs(after).ShouldBe(
        [
            ("moved-org", "a-b", "x-y_1"),
            ("moved-org-other-dataset", "a-b", "kept"),
            ("case-only", "plot", "survey"),
            ("untouched", "stable", "x-y"),
            ("untouched-too", "stable", "kept")
        ], ignoreOrder: true);
    }

    private static (string JobId, string Org, string Dataset)[] Jobs(RegistryContext context) =>
        context.JobIndices.AsNoTracking().ToArray().Select(j => (j.JobId, j.OrgSlug, j.DsSlug)).ToArray();

    // "a--b" has "x--y" (becomes "x-y_1": "x-y" is taken) and "kept"; "Plot" is renamed in case only; "stable" is valid
    // and has a dataset "x-y" of its own, which is not the one renamed to "x-y_1": its jobs stay where they are
    private static async Task SeedAsync(RegistryContext context)
    {
        var now = DateTime.UtcNow;
        context.Organizations.AddRange(
            new Organization
            {
                Slug = "a--b", Name = "A B", CreationDate = now, OwnerId = "owner",
                Datasets =
                [
                    new Dataset { Slug = "x--y", InternalRef = Guid.NewGuid(), CreationDate = now },
                    new Dataset { Slug = "x-y", InternalRef = Guid.NewGuid(), CreationDate = now },
                    new Dataset { Slug = "kept", InternalRef = Guid.NewGuid(), CreationDate = now }
                ]
            },
            new Organization
            {
                Slug = "Plot", Name = "Plot", CreationDate = now, OwnerId = "owner",
                Datasets = [new Dataset { Slug = "survey", InternalRef = Guid.NewGuid(), CreationDate = now }]
            },
            new Organization
            {
                Slug = "stable", Name = "Stable", CreationDate = now, OwnerId = "owner",
                Datasets =
                [
                    new Dataset { Slug = "x-y", InternalRef = Guid.NewGuid(), CreationDate = now },
                    new Dataset { Slug = "kept", InternalRef = Guid.NewGuid(), CreationDate = now }
                ]
            });
        context.JobIndices.AddRange(
            Job("moved-org", "a--b", "x--y"),
            Job("moved-org-other-dataset", "a--b", "kept"),
            Job("case-only", "Plot", "survey"),
            Job("untouched", "stable", "x-y"),
            Job("untouched-too", "stable", "kept"));
        await context.SaveChangesAsync();

        static JobIndex Job(string id, string org, string dataset) => new()
        {
            JobId = id, OrgSlug = org, DsSlug = dataset, CurrentState = "Succeeded", CreatedAtUtc = DateTime.UtcNow
        };
    }
}
