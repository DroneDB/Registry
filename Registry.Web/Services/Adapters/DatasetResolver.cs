using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Adapters;

/// <summary>Default <see cref="IDatasetResolver"/> over the registry database and the dataset lookup cache.</summary>
public class DatasetResolver : IDatasetResolver
{
    private readonly RegistryContext _context;
    private readonly IDatasetLookupCache _lookups;

    public DatasetResolver(RegistryContext context, IDatasetLookupCache lookups)
    {
        _context = context;
        _lookups = lookups;
    }

    public async Task<Organization> GetOrganizationAsync(string orgSlug, bool safe = false, bool withTracking = false,
        bool withDatasets = false, CancellationToken ct = default)
    {
        EnsureSlug(orgSlug, "organization");

        // Eagerly loading Datasets joins (potentially) thousands of rows on every lookup;
        // most callers only need the Organization row itself. Opt in via withDatasets.
        IQueryable<Organization> query = _context.Organizations;
        if (withDatasets)
            query = query.Include(item => item.Datasets);

        // Apply AsNoTracking only if withTracking is false (default behavior for read-only operations)
        if (!withTracking)
            query = query.AsNoTracking();

        var org = await query.FirstOrDefaultAsync(item => item.Slug == orgSlug, ct);

        if (org == null)
            return safe ? null : throw new NotFoundException("Organization not found");

        return org;
    }

    public async Task<Dataset> GetDatasetAsync(string orgSlug, string dsSlug, bool safe = false,
        bool withTracking = false, CancellationToken ct = default)
    {
        EnsureSlug(dsSlug, "dataset");
        EnsureSlug(orgSlug, "organization");

        Task<Dataset> Load(CancellationToken token)
        {
            IQueryable<Dataset> query = _context.Datasets.Include(item => item.Organization);

            // Apply AsNoTracking only if withTracking is false (default behavior for read-only operations)
            if (!withTracking)
                query = query.AsNoTracking();

            return query.FirstOrDefaultAsync(item => item.Slug == dsSlug && item.Organization.Slug == orgSlug, token);
        }

        // With the organization's members, which the access check of a member needs: a cached
        // lookup then answers it without a query
        Task<Dataset> LoadWithMembers(CancellationToken token) => _context.Datasets
            .Include(item => item.Organization)
            .ThenInclude(org => org.Users)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Slug == dsSlug && item.Organization.Slug == orgSlug, token);

        // Read-only lookups repeat on every request: reuse them (REG-05). A tracked entity is for
        // an update, so it always comes from the database.
        var dataset = withTracking
            ? await Load(ct)
            : await _lookups.GetOrLoadAsync(orgSlug, dsSlug, LoadWithMembers, ct);

        return dataset ?? (safe ? null : throw new NotFoundException($"Cannot find dataset {dsSlug} in organization {orgSlug}"));
    }

    // "Missing organization id", "Invalid dataset id", ...: the messages clients have always seen
    private static void EnsureSlug(string slug, string kind)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new BadRequestException($"Missing {kind} id");

        if (!slug.IsValidSlug())
            throw new BadRequestException($"Invalid {kind} id");
    }

    public async Task<string> GetFreeOrganizationSlugAsync(string orgName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orgName))
            throw new BadRequestException("Empty organization name");

        var slug = orgName.ToSlug();

        var res = slug;

        for (var n = 1;; n++)
        {
            if (!await _context.Organizations.AsNoTracking().AnyAsync(item => item.Slug == res, ct)) return res;

            res = slug + "-" + n;
        }
    }
}
