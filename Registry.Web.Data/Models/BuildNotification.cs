#nullable enable
using System;

namespace Registry.Web.Data.Models;

/// <summary>
/// One line of a build notification e-mail waiting to be sent (an outbox row): a build of a user's entry that ended. The
/// lines of one user are sent together in one e-mail a few minutes after the first one, then kept a while as the record
/// of what was sent, so the same answer to the same request is never sent twice.
/// </summary>
public class BuildNotification : IDatasetSlugKeyed
{
    /// <summary>The longest <see cref="Error"/> kept.</summary>
    public const int ErrorMaxLength = 500;

    /// <summary>Row id.</summary>
    public long Id { get; set; }

    /// <summary>The user the line is for (<c>AspNetUsers.Id</c>).</summary>
    public string UserId { get; set; } = null!;

    /// <summary>Organization slug of the dataset.</summary>
    public string OrgSlug { get; set; } = null!;

    /// <summary>Dataset slug.</summary>
    public string DsSlug { get; set; } = null!;

    /// <summary>The entry that was built, relative to the dataset root.</summary>
    public string Path { get; set; } = null!;

    /// <summary>The hash of the entry that was built.</summary>
    public string Hash { get; set; } = null!;

    /// <summary>How the build ended: <c>Completed</c> or <c>Failed</c>.</summary>
    public string Outcome { get; set; } = null!;

    /// <summary>A short reason when it failed, with no server path in it; null otherwise.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The job of the request the line answers: the build job itself, or, for an automatic retry, the user's build it
    /// retries. One line per request and outcome (a unique key), so a retry chain that gives up again, or a rebuild of the
    /// same content, does not repeat it.
    /// </summary>
    public string? RequestJobId { get; set; }

    /// <summary>When the build ended (UTC).</summary>
    public DateTime FinishedAtUtc { get; set; }

    /// <summary>
    /// Set while the line is unsent: a digest of who, which entry (path and hash) and which outcome. Unique, so the same
    /// news is waiting at most once whatever requests led to it; cleared when the line is sent. A nullable column with a
    /// plain unique index works the same on SQLite and MySQL/MariaDB (both allow any number of NULLs), where a filtered
    /// unique index would not, and its fixed length fits the MySQL index key limit that the path would not.
    /// </summary>
    public string? PendingKey { get; set; }

    /// <summary>The digest run that is sending the line; null while no run holds it.</summary>
    public string? ClaimId { get; set; }

    /// <summary>When <see cref="ClaimId"/> was set (UTC), to release a claim whose run died.</summary>
    public DateTime? ClaimedAtUtc { get; set; }

    /// <summary>When the line was sent, or dropped because the user no longer wants it (UTC); null while it waits.</summary>
    public DateTime? SentAtUtc { get; set; }
}
