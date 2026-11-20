using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Datasets;

/// <summary>
/// The creation of a dataset once its creation is allowed: the user path (<see cref="DatasetCommands.AddNew"/>, after
/// the organization write access check) and the system path of a task that creates the dataset for its results
/// (photogrammetry "Create new dataset for results", <see cref="CreateAsSystemAsync"/>, D-031), which has no current
/// user: the submit and the worker check the access of the requesting user. One slug rule (<see cref="SlugRules"/>) and one unique
/// check for both. Registered on every job host.
/// </summary>
public sealed class DatasetCreation
{
    private readonly RegistryContext _context;
    private readonly IDdbManager _ddbManager;
    private readonly ILogger? _logger;

    public DatasetCreation(RegistryContext context, IDdbManager ddbManager, ILogger<DatasetCreation>? logger = null)
        : this(context, ddbManager, (ILogger?)logger)
    {
    }

    /// <summary>The creation of a caller that logs to its own logger (<see cref="DatasetCommands"/>).</summary>
    internal DatasetCreation(RegistryContext context, IDdbManager ddbManager, ILogger? logger)
    {
        _context = context;
        _ddbManager = ddbManager;
        _logger = logger;
    }

    /// <summary>Whether a dataset of <paramref name="orgSlug"/> already has <paramref name="slug"/>.</summary>
    public Task<bool> SlugIsTakenAsync(string orgSlug, string slug, CancellationToken ct = default) =>
        _context.Datasets.AsNoTracking().AnyAsync(item => item.Slug == slug && item.Organization.Slug == orgSlug, ct);

    /// <summary>
    /// The current slug of the dataset of <paramref name="orgSlug"/> whose folder id is <paramref name="internalRef"/>, or
    /// null when that dataset no longer exists (deleted, or moved to another organization).
    /// </summary>
    public Task<string?> ExistingSlugAsync(string orgSlug, Guid internalRef, CancellationToken ct = default) =>
        _context.Datasets.AsNoTracking()
            .Where(item => item.InternalRef == internalRef && item.Organization.Slug == orgSlug)
            .Select(item => item.Slug)
            .FirstOrDefaultAsync(ct);

    /// <summary>The 400 of a creation that finds its slug taken.</summary>
    public static BadRequestException SlugTakenOnCreate(Exception? inner = null) =>
        new("Dataset with this slug already exists", innerException: inner)
        {
            // What must be unique is the slug: the form shows the refusal there (D-047, FR-ORG-04)
            FieldErrors = new Dictionary<string, string[]> { ["slug"] = ["Dataset with this slug already exists"] }
        };

    /// <summary>
    /// Creates the dataset <paramref name="dataset"/> in <paramref name="org"/> (tracked, with its datasets) without an
    /// access check: the callers made it. <paramref name="slugTaken"/> builds the error of a slug that is taken
    /// (<see cref="SlugTakenOnCreate"/> by default).
    /// </summary>
    public async Task<CreatedDatasetInfo> CreateAsync(Organization org, DatasetNewDto dataset,
        Func<Exception?, BadRequestException>? slugTaken = null, CancellationToken ct = default)
    {
        slugTaken ??= SlugTakenOnCreate;

        if (dataset == null)
            throw new BadRequestException("Dataset is null");

        // A name with no Latin letter or digit needs a slug the request gives (D-035): the name's refusal comes first
        InputRules.EnsureLatinName(dataset.Name, dataset.Slug, "name");

        if (dataset.Slug == null)
            throw new BadRequestException("Dataset slug is null");

        if (!dataset.Slug.IsValidSlug())
            throw new BadRequestException(SlugRules.InvalidMessage("Dataset"));

        if (await SlugIsTakenAsync(org.Slug, dataset.Slug, ct))
            throw slugTaken(null);

        var ds = new Dataset
        {
            Slug = dataset.Slug,
            Organization = org,
            InternalRef = Guid.NewGuid(),
            CreationDate = DateTime.UtcNow
        };

        var ddb = _ddbManager.Get(org.Slug, ds.InternalRef);
        var meta = ddb.Meta.GetSafe();

        meta.Name = string.IsNullOrWhiteSpace(dataset.Name) ? dataset.Slug : dataset.Name;

        if (dataset.Visibility.HasValue)
            meta.Visibility = dataset.Visibility.Value;

        if (!string.IsNullOrWhiteSpace(dataset.Tagline))
            meta.Tagline = dataset.Tagline;

        org.Datasets.Add(ds);

        try
        {
            await SaveUniqueSlugAsync(org.Slug, ds.Slug, slugTaken);
        }
        catch (BadRequestException)
        {
            // Another request saved the slug first: the folder and the meta written above belong to no dataset
            DeleteUnsavedFolder(org.Slug, ds.InternalRef);
            throw;
        }

        return new CreatedDatasetInfo(ds.ToDto(ddb.GetInfo()), ds.InternalRef);
    }

    // The folder of a dataset whose row was not saved. A failure to delete it does not hide the answer to the creation
    // (the slug is taken): it is logged, and the orphaned-folder cleanup removes it later
    private void DeleteUnsavedFolder(string orgSlug, Guid internalRef)
    {
        try
        {
            _ddbManager.Delete(orgSlug, internalRef);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger?.LogWarning(ex, "Cannot delete the folder of the dataset {InternalRef} of {OrgSlug} that lost its slug",
                internalRef, orgSlug);
        }
    }

    /// <summary>
    /// Creates the dataset in the organization <paramref name="orgSlug"/> for a task, on behalf of the user who
    /// submitted it (the worker has none, and checked that user's access before): no access check here (D-031). <paramref name="slugTaken"/> as in
    /// <see cref="CreateAsync"/>.
    /// </summary>
    /// <exception cref="NotFoundException">The organization does not exist.</exception>
    public async Task<CreatedDatasetInfo> CreateAsSystemAsync(string orgSlug, DatasetNewDto dataset,
        Func<Exception?, BadRequestException>? slugTaken = null, CancellationToken ct = default)
    {
        var org = await _context.Organizations.AsTracking().Include(item => item.Datasets)
                      .FirstOrDefaultAsync(item => item.Slug == orgSlug, ct)
                  ?? throw new NotFoundException(DatasetResolverExtensions.OrganizationNotFoundMessage);

        return await CreateAsync(org, dataset, slugTaken, ct);
    }

    /// <summary>
    /// Saves what the caller tracked for a dataset that takes <paramref name="slug"/> in <paramref name="orgSlug"/>. The
    /// check made before is not enough: another request may save the same slug in between, and the unique index of the
    /// database refuses the second. That request is answered as the one that found the slug taken in the check
    /// (<paramref name="taken"/>); a failure with the slug still free is not a lost race and propagates.
    /// </summary>
    public async Task SaveUniqueSlugAsync(string orgSlug, string slug, Func<Exception, BadRequestException> taken)
    {
        try
        {
            await _context.SaveChangesAsync(CancellationToken.None);
        }
        catch (DbUpdateException ex)
        {
            // What the context tracked did not reach the database: ask it fresh whether the slug is the reason
            _context.ChangeTracker.Clear();
            if (await SlugIsTakenAsync(orgSlug, slug, CancellationToken.None))
                throw taken(ex);

            throw;
        }
    }
}

/// <summary>A dataset just created: its DTO and its folder id (the DTO does not carry it).</summary>
public sealed record CreatedDatasetInfo(DatasetDto Dataset, Guid InternalRef);
