using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Services.BuildNotifications;
using Shouldly;

namespace Registry.Web.Test.Support;

/// <summary>
/// What the build notification outbox needs from a migrated database, the same checks on every provider (SQLite here,
/// the MariaDB of production in the integration tests): the unique keys that keep the same news from waiting twice
/// (a nullable column, so any number of sent lines may hold none) and the conditional updates of a digest run, which
/// are translated per provider.
/// </summary>
public static class BuildNotificationOutboxChecks
{
    private static readonly DateTime At = new(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);

    private static BuildNotification Line(string requestJobId, string? pendingKey, DateTime? sent = null) => new()
    {
        UserId = "jane", OrgSlug = "org", DsSlug = "ds", Path = "cloud.laz", Hash = "cloud-hash", Outcome = "Failed",
        Error = "it broke", RequestJobId = requestJobId, FinishedAtUtc = At, PendingKey = pendingKey, SentAtUtc = sent
    };

    /// <summary>
    /// The table holds any number of sent lines (no pending key), refuses a second waiting line with the same pending
    /// key and a second line of the same request and outcome, and a digest run claims and sends the waiting lines.
    /// </summary>
    /// <param name="newContext">A new context on the migrated database.</param>
    public static async Task AssertTheOutboxWorksAsync(Func<RegistryContext> newContext)
    {
        await using (var db = newContext())
        {
            db.BuildNotifications.AddRange(Line("sent-1", null, At), Line("sent-2", null, At), Line("waiting", "key-1"));
            await db.SaveChangesAsync();
        }

        await ShouldRefuseAsync(newContext, Line("other-request", "key-1"), "the same news waits once");
        await ShouldRefuseAsync(newContext, Line("waiting", "key-2"), "one line per request and outcome");

        await using (var db = newContext())
        {
            var outbox = new BuildNotificationOutbox(db, TimeProvider.System);
            (await outbox.ClaimAsync("jane", "run-1", CancellationToken.None)).Select(l => l.RequestJobId)
                .ShouldBe(["waiting"]);
            (await outbox.ClaimAsync("jane", "run-2", CancellationToken.None)).ShouldBeEmpty("a claimed line is not claimed twice");
            await outbox.MarkSentAsync("run-1", CancellationToken.None);
        }

        await using (var db = newContext())
        {
            var sent = await db.BuildNotifications.AsNoTracking().SingleAsync(l => l.RequestJobId == "waiting");
            sent.SentAtUtc.ShouldNotBeNull();
            sent.PendingKey.ShouldBeNull();

            // Sent, the same news may wait again
            db.BuildNotifications.Add(Line("again", "key-1"));
            await db.SaveChangesAsync();
        }
    }

    private static async Task ShouldRefuseAsync(Func<RegistryContext> newContext, BuildNotification line, string why)
    {
        await using var db = newContext();
        db.BuildNotifications.Add(line);
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(), why);
    }
}
