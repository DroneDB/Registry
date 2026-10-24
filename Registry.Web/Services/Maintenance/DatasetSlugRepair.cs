using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Caching;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Maintenance;

/// <summary>
/// Repairs the organizations and datasets stored with a slug the slug rules (<see cref="SlugRules"/>) refuse: "--"
/// from the pre-collapsing generator, or one character from before the validator agreed with the routes.
/// </summary>
public interface IDatasetSlugRepair
{
    /// <summary>
    /// Renames organizations and datasets whose slug fails validation (e.g. "--" from the
    /// pre-collapsing slug generator, or a single character) to their ToSlug form, moving organization folders.
    /// With dryRun only the plan is returned. Entries with active jobs are skipped.
    /// </summary>
    Task<FixInvalidSlugsResultDto> FixInvalidSlugsAsync(bool dryRun = true, CancellationToken ct = default);
}

/// <summary>Default <see cref="IDatasetSlugRepair"/> (RQ-12, out of <c>SystemManager</c>).</summary>
public sealed class DatasetSlugRepair : IDatasetSlugRepair
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly IFileSystem _fileSystem;
    private readonly StorageOptions _storage;
    private readonly IDatasetListingInvalidator _listings;
    private readonly ILogger<DatasetSlugRepair> _logger;

    public DatasetSlugRepair(RegistryContext context, IDdbManager ddbManager, IFileSystem fileSystem,
        IOptions<StorageOptions> storage, IDatasetListingInvalidator listings, ILogger<DatasetSlugRepair> logger)
    {
        _context = context;
        _ddbManager = ddbManager;
        _fileSystem = fileSystem;
        _storage = storage.Value;
        _listings = listings;
        _logger = logger;
    }

    // Job states during which a dataset's slug and folder must not change under the job
    private static readonly string[] SlugFixBlockingJobStates =
        ["Created", "Indexing", "Enqueued", "Scheduled", "Processing", "Awaiting"];

    public async Task<FixInvalidSlugsResultDto> FixInvalidSlugsAsync(bool dryRun = true, CancellationToken ct = default)
    {
        var result = new FixInvalidSlugsResultDto { DryRun = dryRun };

        // The token is honoured while the targets are read; the renames then run to completion (RQ-13)
        var orgs = await _context.Organizations.AsNoTracking().Include(o => o.Datasets).ToArrayAsync(ct);
        var takenOrgSlugs = orgs.Select(o => o.Slug).ToHashSet();

        var busy = (await _context.JobIndices.AsNoTracking()
                .Where(j => SlugFixBlockingJobStates.Contains(j.CurrentState))
                .Select(j => new { j.OrgSlug, j.DsSlug })
                .ToArrayAsync(ct))
            .Select(j => (j.OrgSlug, j.DsSlug))
            .ToHashSet();

        foreach (var org in orgs)
        {
            var renameOrg = !org.Slug.IsValidSlug();
            var badDatasets = org.Datasets.Where(d => !d.Slug.IsValidSlug()).ToArray();
            if (!renameOrg && badDatasets.Length == 0) continue;

            string newOrgSlug = null;
            if (renameOrg)
            {
                newOrgSlug = FreeSlug(org.Slug, takenOrgSlugs);
                takenOrgSlugs.Remove(org.Slug);
                takenOrgSlugs.Add(newOrgSlug);
            }

            var takenDsSlugs = org.Datasets.Select(d => d.Slug).ToHashSet();
            var dsRenames = new Dictionary<string, string>();
            foreach (var ds in badDatasets)
            {
                dsRenames[ds.Slug] = FreeSlug(ds.Slug, takenDsSlugs);
                takenDsSlugs.Remove(ds.Slug);
                takenDsSlugs.Add(dsRenames[ds.Slug]);
            }

            var entries = new List<SlugFixEntryDto>();
            if (renameOrg)
                entries.Add(new SlugFixEntryDto
                {
                    Kind = "organization", OrganizationSlug = org.Slug, OldSlug = org.Slug, NewSlug = newOrgSlug
                });
            entries.AddRange(dsRenames.Select(r => new SlugFixEntryDto
            {
                Kind = "dataset", OrganizationSlug = org.Slug, OldSlug = r.Key, NewSlug = r.Value
            }));
            result.Entries.AddRange(entries);

            // An organization rename moves the folder of every dataset in it
            var blocked = renameOrg
                ? org.Datasets.Any(d => busy.Contains((org.Slug, d.Slug)))
                : dsRenames.Keys.Any(s => busy.Contains((org.Slug, s)));

            if (dryRun)
            {
                entries.ForEach(e =>
                {
                    e.Status = blocked ? "skipped" : "planned";
                    e.Reason = blocked ? "active jobs" : null;
                });
                continue;
            }

            if (blocked)
            {
                entries.ForEach(e => { e.Status = "skipped"; e.Reason = "active jobs, retry when they finish"; });
                continue;
            }

            try
            {
                await ApplySlugFix(org.Slug, newOrgSlug, dsRenames);
                entries.ForEach(e => e.Status = "renamed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot fix slugs of organization '{OrgSlug}'", org.Slug);
                entries.ForEach(e => { e.Status = "failed"; e.Reason = ex.Message; });
                continue;
            }

            // The listings show the slugs (an organization rename changes the slug path of all its datasets)
            foreach (var ds in org.Datasets.Where(d => renameOrg || dsRenames.ContainsKey(d.Slug)))
                await _listings.ListingChangedAsync(org.Slug, ds.InternalRef, CancellationToken.None);
        }

        return result;
    }

    // ToSlug form of an invalid slug (always long enough: "o" is "o0"), suffixed with _N when another slug already takes it.
    // Compared ignoring case: case-insensitive collations (MySQL) see "a-b" and "A-B" as the same key
    private static string FreeSlug(string invalidSlug, IEnumerable<string> taken)
    {
        var others = taken.Where(t => t != invalidSlug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var baseSlug = invalidSlug.ToSlug();
        var slug = baseSlug;
        for (var n = 1; others.Contains(slug); n++)
        {
            var suffix = $"_{n}";
            slug = baseSlug[..Math.Min(baseSlug.Length, 128 - suffix.Length)] + suffix;
        }

        if (!slug.IsValidSlug())
            throw new InvalidOperationException($"Cannot derive a valid slug from '{invalidSlug}'");

        return slug;
    }

    // Renames one organization (newOrgSlug != null) and/or some of its datasets in one transaction.
    // The organization folder is moved first and moved back if the database update fails.
    private async Task ApplySlugFix(string orgSlug, string newOrgSlug, IReadOnlyDictionary<string, string> dsRenames)
    {
        var oldPath = Path.Combine(_storage.DatasetsPath, orgSlug);
        var newPath = newOrgSlug != null ? Path.Combine(_storage.DatasetsPath, newOrgSlug) : null;
        var folderMoved = false;

        if (newPath != null && _fileSystem.FolderExists(oldPath))
        {
            _ddbManager.ReleaseConnections(oldPath);
            MoveFolder(oldPath, newPath);
            folderMoved = true;
        }

        try
        {
            // Retrying execution strategies (MySQL) refuse user-initiated transactions: the strategy owns the
            // transaction and replays the whole unit after a transient failure. Others run it once.
            await _context.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(
                ct => RenameSlugRows(orgSlug, newOrgSlug, dsRenames, ct),
                ct => SlugRowsRenamed(orgSlug, newOrgSlug, dsRenames, ct));
        }
        catch
        {
            _context.ChangeTracker.Clear();
            if (folderMoved)
                MoveFolder(newPath, oldPath);
            throw;
        }

        _logger.LogInformation("Fixed slugs of organization '{OrgSlug}' (new slug: {NewOrgSlug}, datasets: {Datasets})",
            orgSlug, newOrgSlug ?? orgSlug, string.Join(", ", dsRenames.Select(r => $"{r.Key} -> {r.Value}")));
    }

    // Database part of ApplySlugFix, run inside the transaction of the execution strategy
    private async Task RenameSlugRows(string orgSlug, string newOrgSlug,
        IReadOnlyDictionary<string, string> dsRenames, CancellationToken ct)
    {
        // A replay after a transient failure starts from the database, not from the failed attempt
        _context.ChangeTracker.Clear();

        var org = await _context.Organizations
            .Include(o => o.Datasets)
            .Include(o => o.Users)
            .AsSplitQuery()
            .FirstAsync(o => o.Slug == orgSlug, ct);
        var datasets = org.Datasets.ToArray();

        if (newOrgSlug != null)
        {
            // Case-insensitive collations (MySQL) see "abc" as taken by "ABC": hop through a temporary slug
            if (string.Equals(orgSlug, newOrgSlug, StringComparison.OrdinalIgnoreCase))
                org = await RepointOrganization(org, datasets, "slugfix-" + Guid.NewGuid().ToString("N"), ct);

            await RepointOrganization(org, datasets, newOrgSlug, ct);
        }

        foreach (var ds in datasets)
            if (dsRenames.TryGetValue(ds.Slug, out var newDsSlug))
                ds.Slug = newDsSlug;

        // The job history follows its dataset (the datasets first: their rows then no longer carry the old
        // organization slug, which moves only the rows of the datasets that keep their slug)
        foreach (var (oldDsSlug, newDsSlug) in dsRenames)
            await _context.FollowDatasetAsync(orgSlug, oldDsSlug, newOrgSlug ?? orgSlug, newDsSlug, ct);
        if (newOrgSlug != null)
            await _context.FollowOrganizationAsync(orgSlug, newOrgSlug, ct);

        await _context.SaveChangesAsync(ct);
    }

    // The slug is the primary key: create the organization under the new slug, repoint its members and
    // datasets, then drop the old row
    private async Task<Organization> RepointOrganization(Organization org, IReadOnlyCollection<Dataset> datasets,
        string newSlug, CancellationToken ct)
    {
        var target = new Organization
        {
            Slug = newSlug,
            Name = org.Name,
            Description = org.Description,
            CreationDate = org.CreationDate,
            OwnerId = org.OwnerId,
            IsPublic = org.IsPublic,
            Users = []
        };
        _context.Organizations.Add(target);

        foreach (var user in org.Users?.ToArray() ?? [])
        {
            target.Users.Add(new OrganizationUser
            {
                Organization = target,
                OrganizationSlug = newSlug,
                UserId = user.UserId,
                Permissions = user.Permissions,
                GrantedAt = user.GrantedAt,
                GrantedBy = user.GrantedBy
            });
            _context.OrganizationsUsers.Remove(user);
        }

        foreach (var ds in datasets)
            ds.Organization = target;

        await _context.SaveChangesAsync(ct);

        // Deleting cascades to datasets: only once they all point to the new organization
        _context.Organizations.Remove(org);
        await _context.SaveChangesAsync(ct);

        return target;
    }

    // Asked by the execution strategy when a commit failed transiently: whether it reached the database anyway.
    // Slugs are compared in memory because case-insensitive collations match "abc" and "ABC" in SQL.
    private async Task<bool> SlugRowsRenamed(string orgSlug, string newOrgSlug,
        IReadOnlyDictionary<string, string> dsRenames, CancellationToken ct)
    {
        var finalOrgSlug = newOrgSlug ?? orgSlug;

        var org = await _context.Organizations.AsNoTracking()
            .Where(o => o.Slug == finalOrgSlug)
            .Select(o => new { o.Slug, Datasets = o.Datasets.Select(d => d.Slug).ToArray() })
            .FirstOrDefaultAsync(ct);

        return org != null && org.Slug == finalOrgSlug &&
               dsRenames.All(r => !org.Datasets.Contains(r.Key) && org.Datasets.Contains(r.Value));
    }

    // Case-insensitive file systems (Windows, macOS) see the target of a case-only rename as existing:
    // such a move hops through a temporary name
    private void MoveFolder(string from, string to)
    {
        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            if (_fileSystem.FolderExists(to))
                throw new ConflictException($"Folder '{to}' already exists");

            _fileSystem.FolderMove(from, to);
            return;
        }

        var hop = from + ".slugfix-" + Guid.NewGuid().ToString("N");
        _fileSystem.FolderMove(from, hop);
        try
        {
            _fileSystem.FolderMove(hop, to);
        }
        catch
        {
            _fileSystem.FolderMove(hop, from);
            throw;
        }
    }
}
