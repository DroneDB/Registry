#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Models.Configuration;
using Registry.Web.Test.Support;
using Registry.Web.Utilities;
using Shouldly;

namespace Registry.Web.Test.ImprovementBaselines;

/// <summary>
/// RQ-04 item "HasPendingModelChanges() == false for each provider": a model change without its
/// migration only showed up when an operator ran the next upgrade. Each context is configured
/// exactly as the host does it (<see cref="StartupExtenders.AddDbContextWithProvider{T}"/>: the
/// provider, the <c>Registry.Web.{Data|Identity}.{Sqlite|MySql}Migrations</c> assembly), and the
/// current model is compared with the snapshot of that migrations assembly. No database is opened:
/// SQLite needs none for the comparison, and MySQL uses a pinned server version
/// (<see cref="MySqlServerVersionCache.PinnedVersionKey"/>), so it never probes a server.
/// <para>
/// A failure means a migration is missing for that provider: add it against the named assembly
/// through the DebugEF design-time host, as the commands in <c>Program.cs</c> show (<c>dotnet ef
/// migrations add Name --project &lt;assembly&gt; --context &lt;context&gt; --configuration DebugEF
/// --startup-project Registry.Web -- --provider Sqlite|MySql</c>); do not change this test.
/// </para>
/// Run: dotnet test Registry.Web.Test --filter "FullyQualifiedName~Rq04ModelChangesTests"
/// </summary>
[TestFixture]
[Category("ImprovementBaseline")]
public class Rq04ModelChangesTests
{
    // Production runs MariaDB 10.6 (docker/production); MySQL 8 is the other supported server
    private static readonly string[] MySqlVersions = ["10.6.0-mariadb", "8.0.36-mysql"];

    private static IEnumerable<TestCaseData> Cases(string context)
    {
        yield return new TestCaseData(DbProvider.Sqlite, null).SetName($"{context}, Sqlite");
        foreach (var version in MySqlVersions)
            yield return new TestCaseData(DbProvider.Mysql, version).SetName($"{context}, MySql {version}");
    }

    [TestCaseSource(nameof(Cases), new object[] { nameof(RegistryContext) })]
    public void Registry_HasNoPendingModelChanges(DbProvider provider, string? serverVersion) =>
        AssertNoPendingModelChanges<RegistryContext>(provider, serverVersion);

    [TestCaseSource(nameof(Cases), new object[] { nameof(ApplicationDbContext) })]
    public void Identity_HasNoPendingModelChanges(DbProvider provider, string? serverVersion) =>
        AssertNoPendingModelChanges<ApplicationDbContext>(provider, serverVersion);

    private static void AssertNoPendingModelChanges<TContext>(DbProvider provider, string? serverVersion)
        where TContext : DbContext
    {
        // Never opened: the comparison needs the provider, not a database
        var connectionString = provider == DbProvider.Sqlite
            ? "Data Source=model-changes.sqlite"
            : "Server=127.0.0.1;Port=1;Database=ModelChanges;Uid=u;Pwd=p;";
        using var root = HostDbContexts.Build<TContext>(provider, connectionString, serverVersion);
        using var scope = root.CreateScope();
        using var context = scope.ServiceProvider.GetRequiredService<TContext>();

        var expectedAssembly = HostDbContexts.MigrationsAssembly<TContext>(provider);
        var migrations = context.GetService<IMigrationsAssembly>();
        migrations.Assembly.GetName().Name.ShouldBe(expectedAssembly);
        migrations.ModelSnapshot.ShouldNotBeNull($"{expectedAssembly} has no model snapshot");

        if (context.Database.HasPendingModelChanges())
            Assert.Fail($"{typeof(TContext).Name} on {provider} {serverVersion}: the model differs from the " +
                        $"{expectedAssembly} snapshot (missing migration):\n" + Differences(context, migrations));
    }

    /// <summary>The migration operations a new migration would contain, one per line.</summary>
    private static string Differences(DbContext context, IMigrationsAssembly migrations)
    {
        var differ = context.GetService<IMigrationsModelDiffer>();
        var initializer = context.GetService<IModelRuntimeInitializer>();
        // As HasPendingModelChanges does; the snapshot model is already finalized by that call
        var snapshot = migrations.ModelSnapshot!.Model;
        if (snapshot is IMutableModel mutable and Annotatable { IsReadOnly: false })
            snapshot = mutable.FinalizeModel();
        snapshot = initializer.Initialize(snapshot);
        var current = context.GetService<IDesignTimeModel>().Model;

        return string.Join("\n", differ.GetDifferences(snapshot.GetRelationalModel(), current.GetRelationalModel())
            .Select(o => "  " + Describe(o)));
    }

    private static string Describe(MigrationOperation operation) => operation switch
    {
        AlterColumnOperation a =>
            $"{operation.GetType().Name} {a.Table}.{a.Name}: {Column(a.OldColumn)} -> {Column(a)}",
        UpdateDataOperation u =>
            $"{operation.GetType().Name} {u.Table} key ({string.Join(", ", u.KeyColumns)}) = ({Values(u.KeyValues)}): " +
            $"({string.Join(", ", u.Columns)}) = ({Values(u.Values)})",
        ColumnOperation c =>
            $"{operation.GetType().Name} {c.Table}.{c.Name} ({c.ColumnType}, nullable={c.IsNullable})",
        TableOperation t => $"{operation.GetType().Name} {t.Name}",
        CreateIndexOperation i =>
            $"{operation.GetType().Name} {i.Table}.{i.Name} ({string.Join(", ", i.Columns)})",
        DropIndexOperation i =>
            $"{operation.GetType().Name} {i.Table}.{i.Name}",
        _ => operation.GetType().Name
    };

    private static string Column(ColumnOperation c) =>
        $"{c.ColumnType ?? c.ClrType.Name}, nullable={c.IsNullable}, default={c.DefaultValue ?? c.DefaultValueSql ?? "none"}, " +
        $"annotations=[{string.Join("; ", c.GetAnnotations().Select(a => $"{a.Name}={a.Value}"))}]";

    private static string Values(object?[,] values) =>
        string.Join(" | ", Enumerable.Range(0, values.GetLength(0)).Select(row =>
            string.Join(", ", Enumerable.Range(0, values.GetLength(1)).Select(col => values[row, col] ?? "null"))));
}
