using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Registry.Common;
using Registry.Test.Common;
using Registry.Web.Data;
using Registry.Web.Identity;
using Registry.Web.Test.Support;
using Shouldly;

namespace Registry.Web.Test.Baselines;

/// <summary>
/// The SQLite <c>AddUserCreatedDate</c> migration (FR-ADM-19, SPEC §8.3), run for real: a user that exists before the
/// column reads <c>CreatedDate</c> as null (the date is not known, the Hub shows "Unknown"), a date written through
/// the model reads back in UTC, and the migration reverts (the column goes, the user stays). The MySQL migration is
/// covered on MariaDB (<c>UserCreatedDateMigrationMariaDbTests</c>, Registry.Web.IntegrationTest).
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~UserCreatedDateMigrationTests"
/// </summary>
[TestFixture]
[NonParallelizable]
[Category(TestCategories.Baseline)]
public class UserCreatedDateMigrationTests
{
    private const string AddUserCreatedDate = "_AddUserCreatedDate";

    private string _root = null!;
    private ServiceProvider _services = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "usercreateddatemigration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "identity.db"), Pooling = false
        }.ToString();
        _services = HostDbContexts.Build<ApplicationDbContext>(DbProvider.Sqlite, connectionString);
    }

    [TearDown]
    public void TearDown()
    {
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private static string Migration(DbContext context) =>
        context.Database.GetMigrations().Single(m => m.EndsWith(AddUserCreatedDate, StringComparison.Ordinal));

    private static string PreviousMigration(DbContext context) =>
        context.Database.GetMigrations().TakeWhile(m => m != Migration(context)).Last();

    private static Task InsertUserAsync(ApplicationDbContext context, string id) =>
        context.Database.ExecuteSqlRawAsync(
            "INSERT INTO AspNetUsers (Id, UserName, Metadata, Preferences, EmailConfirmed, PhoneNumberConfirmed, " +
            "TwoFactorEnabled, LockoutEnabled, AccessFailedCount) VALUES ({0}, {0}, '{{}}', '{{}}', 0, 0, 0, 0, 0)", id);

    [Test]
    public async Task AUserThatExistedBeforeTheMigration_HasNoCreatedDate_AndANewDateReadsBackInUtc()
    {
        using (var scope = _services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await context.GetService<IMigrator>().MigrateAsync(PreviousMigration(context));
            (await Columns(context)).ShouldNotContain(c => c.Name == "CreatedDate");
            await InsertUserAsync(context, "old-user");
        }

        // As DatabaseInitializer does it
        using (var scope = _services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.SafeMigrateAsync();

        using var check = _services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await after.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        after.Database.HasPendingModelChanges().ShouldBeFalse();

        var column = (await Columns(after)).Single(c => c.Name == "CreatedDate");
        column.Type.ShouldBe("TEXT");
        column.IsRequired.ShouldBe(0L, "nullable: a user created before it has no date");

        (await after.Users.AsNoTracking().SingleAsync(u => u.Id == "old-user")).CreatedDate.ShouldBeNull();

        var created = new DateTime(2026, 11, 9, 10, 20, 30, 123, DateTimeKind.Utc);
        var user = await after.Users.SingleAsync(u => u.Id == "old-user");
        user.CreatedDate = created;
        await after.SaveChangesAsync();
        after.ChangeTracker.Clear();
        var read = (await after.Users.AsNoTracking().SingleAsync(u => u.Id == "old-user")).CreatedDate;
        read.ShouldBe(created);
        read!.Value.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Test]
    public async Task TheMigration_Reverts_AndTheUserStays_ThenAppliesAgain()
    {
        using var scope = _services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await context.Database.MigrateAsync();
        await InsertUserAsync(context, "kept-user");
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE AspNetUsers SET CreatedDate = '2026-11-09 10:20:30' WHERE Id = 'kept-user'");

        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration(context));

        (await Columns(context)).ShouldNotContain(c => c.Name == "CreatedDate");
        (await context.Database.SqlQuery<string>($"SELECT Id AS Value FROM AspNetUsers").ToListAsync())
            .ShouldBe(["kept-user"], "Down keeps the users");

        await context.Database.MigrateAsync();
        (await context.Users.AsNoTracking().SingleAsync(u => u.Id == "kept-user")).CreatedDate
            .ShouldBeNull("the values of a reverted column are gone");
    }

    private sealed record ColumnInfo(string Name, string Type, long IsRequired);

    private static async Task<ColumnInfo[]> Columns(ApplicationDbContext context) =>
        (await context.Database.SqlQuery<ColumnInfo>(
            $"""SELECT name AS Name, type AS Type, "notnull" AS IsRequired FROM pragma_table_info('AspNetUsers')""")
            .ToListAsync()).ToArray();
}
