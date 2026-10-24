using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Web.Data.Models;

namespace Registry.Web.Data;

/// <summary>
/// The rows that name a dataset by its organization and dataset slugs instead of pointing at its <c>Datasets</c> row
/// (today only the task index, <see cref="JobIndex"/>: <c>OrgSlug</c>, <c>DsSlug</c>) follow it when it takes a new slug
/// or moves to another organization, so that its job history is still its own afterwards. The one rule for the slug
/// repair, the dataset rename and the move to another organization.
/// </summary>
/// <remarks>
/// Both methods only change the rows the context tracks: the caller saves them in the same
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> as the rename or move itself, which makes them one
/// transaction, retried as a unit by a retrying execution strategy. A new table with a slug column is covered here
/// (the model audit test fails until it is).
/// </remarks>
public static class SlugKeyedRows
{
    /// <summary>The rows of dataset <paramref name="dsSlug"/> of <paramref name="orgSlug"/> become those of
    /// <paramref name="newDsSlug"/> of <paramref name="newOrgSlug"/>. Rows already changed through this context keep
    /// the new slugs, so renames of datasets and of their organization compose.</summary>
    public static Task FollowDatasetAsync(this RegistryContext context, string orgSlug, string dsSlug,
        string newOrgSlug, string newDsSlug, CancellationToken ct = default) =>
        FollowAsync(context,
            context.JobIndices.Where(j => j.OrgSlug == orgSlug && j.DsSlug == dsSlug),
            j => j.OrgSlug == orgSlug && j.DsSlug == dsSlug,
            j => { j.OrgSlug = newOrgSlug; j.DsSlug = newDsSlug; }, ct);

    /// <summary>The rows of every dataset of <paramref name="orgSlug"/> become those of
    /// <paramref name="newOrgSlug"/>.</summary>
    public static Task FollowOrganizationAsync(this RegistryContext context, string orgSlug, string newOrgSlug,
        CancellationToken ct = default) =>
        FollowAsync(context,
            context.JobIndices.Where(j => j.OrgSlug == orgSlug),
            j => j.OrgSlug == orgSlug,
            j => j.OrgSlug = newOrgSlug, ct);

    // The query narrows the rows in the database; the slugs are compared again in memory, ordinally and on the
    // values the row has now: case-insensitive collations (MySQL) match "abc" and "ABC" in SQL, and a row this context
    // already moved still matches its old slugs in the database
    private static async Task FollowAsync(RegistryContext context, IQueryable<JobIndex> candidates,
        Func<JobIndex, bool> isOurs, Action<JobIndex> follow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var row in (await candidates.AsTracking().ToArrayAsync(ct)).Where(isOurs))
            follow(row);
    }
}
