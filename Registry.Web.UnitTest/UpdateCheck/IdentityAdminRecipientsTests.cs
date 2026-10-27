#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Email;
using Registry.Web.Services.Adapters;
using Shouldly;

namespace Registry.Web.Test.UpdateCheck;

/// <summary>
/// The recipients read from a real (SQLite) identity database: the administrators of the update e-mail (the admin role, an
/// address, nobody twice) and one user (<see cref="IdentityUserRecipients"/>), with the same rules for both.
/// Run: dotnet test Registry.Web.UnitTest --filter "FullyQualifiedName~IdentityAdminRecipientsTests"
/// </summary>
[TestFixture]
public class IdentityAdminRecipientsTests : IDisposable
{
    private SqliteConnection _connection = null!;

    [SetUp]
    public async Task SetUp()
    {
        // One open connection keeps the in-memory database alive
        _connection?.Dispose();
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        await using var db = new ApplicationDbContext(Options());
        await db.Database.EnsureCreatedAsync();
        db.Roles.AddRange(new IdentityRole("admin") { NormalizedName = "ADMIN" },
            new IdentityRole("deactivated") { NormalizedName = "DEACTIVATED" });
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        GC.SuppressFinalize(this);
    }

    private DbContextOptions<ApplicationDbContext> Options() =>
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options;

    private async Task AddUser(string name, string? email, params string[] roles)
    {
        await using var db = new ApplicationDbContext(Options());
        var user = new User { Id = name, UserName = name, NormalizedUserName = name.ToUpperInvariant(), Email = email };
        db.Users.Add(user);
        foreach (var role in roles)
            db.UserRoles.Add(new IdentityUserRole<string>
            {
                UserId = user.Id,
                RoleId = (await db.Roles.SingleAsync(r => r.Name == role)).Id
            });
        await db.SaveChangesAsync();
    }

    private static async Task<string[]> Recipients(ApplicationDbContext? identity)
    {
        var recipients = new IdentityAdminRecipients(NullLogger<IdentityAdminRecipients>.Instance, identity);
        return (await recipients.GetAsync(CancellationToken.None)).Select(a => a.Address).ToArray();
    }

    [Test]
    public async Task Recipients_AreTheAdminsThatHaveAnAddress()
    {
        await AddUser("zoe", "zoe@example.com", "admin");
        await AddUser("anna", "anna@example.com", "admin");
        await AddUser("nobody", null, "admin");
        await AddUser("blank", "  ", "admin");
        await AddUser("bob", "bob@example.com");
        await AddUser("gone", "gone@example.com", "admin", "deactivated");

        await using var identity = new ApplicationDbContext(Options());

        (await Recipients(identity)).ShouldBe(["anna@example.com", "zoe@example.com"]);
    }

    [Test]
    public async Task AnAddressThatIsNotOneMailbox_IsLeftOut_AndTheOthersStillGetTheMail()
    {
        await AddUser("list", "a@example.com, b@example.com", "admin");
        await AddUser("named", "Jane <jane@example.com>", "admin");
        await AddUser("ok", "ok@example.com", "admin");
        await AddUser("twin", "OK@example.com", "admin");

        await using var identity = new ApplicationDbContext(Options());

        (await Recipients(identity)).ShouldHaveSingleItem().ShouldBe("ok@example.com", StringCompareShould.IgnoreCase);
    }

    [Test]
    public async Task NoAdmin_NoRecipient()
    {
        await AddUser("bob", "bob@example.com");

        await using var identity = new ApplicationDbContext(Options());

        (await Recipients(identity)).ShouldBeEmpty();
    }

    [Test]
    public async Task AHostWithoutAnIdentityDatabase_HasNoRecipient()
    {
        (await Recipients(null)).ShouldBeEmpty();
    }

    private static Task<EmailAddress?> RecipientOf(ApplicationDbContext? identity, string userId) =>
        new IdentityUserRecipients(NullLogger<IdentityUserRecipients>.Instance, identity).FindAsync(userId, CancellationToken.None);

    [Test]
    public async Task OneUser_IsTheirAddress_NamedAfterThem()
    {
        await AddUser("bob", " bob@example.com ");

        await using var identity = new ApplicationDbContext(Options());

        (await RecipientOf(identity, "bob")).ShouldBe(new EmailAddress("bob@example.com", "bob"));
    }

    [Test]
    public async Task OneUser_FollowsTheSameRules_AsTheAdministrators()
    {
        await AddUser("gone", "gone@example.com", "deactivated");
        await AddUser("nobody", null);
        await AddUser("blank", " ");
        await AddUser("list", "a@example.com, b@example.com");

        await using var identity = new ApplicationDbContext(Options());

        foreach (var id in new[] { "gone", "nobody", "blank", "list", "no-such-user", "" })
            (await RecipientOf(identity, id)).ShouldBeNull(id);
        (await RecipientOf(null, "gone")).ShouldBeNull();
    }
}
