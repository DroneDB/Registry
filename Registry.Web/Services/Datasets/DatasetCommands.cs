using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Adapters;
using Registry.Web.Services.Caching;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Datasets;

/// <summary>The changes to a dataset by its writers: creation, edit, deletion, rename, the legacy public flag. Each renews the listings it changes.</summary>
public sealed class DatasetCommands
{
    private readonly RegistryContext _context;
    private readonly IDatasetResolver _datasets;
    private readonly IDatasetAccessResolver _access;
    private readonly IDdbManager _ddbManager;
    private readonly IDatasetListingInvalidator _listings;
    private readonly DatasetRemoval _removal;
    private readonly DatasetCreation _creation;
    private readonly ILogger<DatasetCommands> _logger;

    public DatasetCommands(RegistryContext context, IDatasetResolver datasets, IDatasetAccessResolver access, IDdbManager ddbManager, IDatasetListingInvalidator listings, DatasetRemoval removal, ILogger<DatasetCommands> logger)
    {
        _context = context;
        _datasets = datasets;
        _access = access;
        _ddbManager = ddbManager;
        _listings = listings;
        _removal = removal;
        // What the creation needs is what this class holds: the system creation of a task (DatasetCreation) is the same code
        _creation = new DatasetCreation(context, ddbManager, (ILogger)logger);
        _logger = logger;
    }

    public async Task<DatasetDto> AddNew(string orgSlug, DatasetNewDto dataset, CancellationToken ct = default)
    {
        var org = await _access.ResolveOrganizationAsync(orgSlug, AccessType.Write, withTracking: true,
            withDatasets: true, ct: ct);

        return (await _creation.CreateAsync(org, dataset, ct: ct)).Dataset;
    }

    public async Task Edit(string orgSlug, string dsSlug, DatasetEditDto dataset, CancellationToken ct = default)
    {
        if (dataset == null)
            throw new BadRequestException("Dataset is null");

        var ds = await _datasets.GetRequiredDatasetAsync(orgSlug, dsSlug, withTracking: true, ct: ct);
        await _access.EnsureAccessAsync(ds, AccessType.Write);

        var ddb = _ddbManager.Get(orgSlug, ds.InternalRef);
        var meta = ddb.Meta.GetSafe();

        if (dataset.Visibility.HasValue)
            meta.Visibility = dataset.Visibility.Value;

        if (!string.IsNullOrWhiteSpace(dataset.Name))
            meta.Name = dataset.Name;

        await _context.SaveChangesAsync(CancellationToken.None);

        // Who may list it (visibility) and how it is listed (name)
        if (dataset.Visibility.HasValue)
            await _listings.VisibilityChangedAsync(orgSlug, ds.InternalRef, CancellationToken.None);
        else if (!string.IsNullOrWhiteSpace(dataset.Name))
            await _listings.ListingChangedAsync(orgSlug, ds.InternalRef, CancellationToken.None);
    }

    public async Task Delete(string orgSlug, string dsSlug, CancellationToken ct = default)
    {
        var ds = await _datasets.GetRequiredDatasetAsync(orgSlug, dsSlug, withTracking: true, ct: ct);
        await _access.EnsureAccessAsync(ds, AccessType.Delete);

        await _removal.RemoveAsync(orgSlug, ds);
    }

    public async Task Rename(string orgSlug, string dsSlug, string newSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(newSlug))
            throw new BadRequestException("New slug is empty");

        if (dsSlug == MagicStrings.DefaultDatasetSlug || newSlug == MagicStrings.DefaultDatasetSlug)
            throw new BadRequestException("Cannot move default dataset");

        if (!newSlug.IsValidSlug())
            throw new BadRequestException(SlugRules.InvalidMessage("Dataset"));

        if (dsSlug == newSlug) return; // Nothing to do

        // Access first: a caller who may not rename learns nothing about the other slugs
        var ds = await _datasets.GetRequiredDatasetAsync(orgSlug, dsSlug, withTracking: true, ct: ct);
        await _access.EnsureAccessAsync(ds, AccessType.Write);

        // Its queued and running jobs name it by the old slug (the same rule as the slug repair)
        await _context.EnsureNoActiveJobsAsync(orgSlug, dsSlug, ct);

        if (await _datasets.GetDatasetAsync(orgSlug, newSlug, safe: true, ct: ct) != null)
            throw SlugTakenOnRename(newSlug);

        ds.Slug = newSlug;

        // Its job history follows the slug, saved with it
        await _context.FollowDatasetAsync(orgSlug, dsSlug, orgSlug, newSlug, ct);

        await SaveUniqueSlugAsync(orgSlug, newSlug, inner => SlugTakenOnRename(newSlug, inner));

        // The listings link to the old slug
        await _listings.ListingChangedAsync(orgSlug, ds.InternalRef, CancellationToken.None);
    }

    // The legacy attribute of the DroneDB CLI's old "chattr": "public" is the visibility, changed like a dataset
    // edit changes it (one rule: the visibility meta and the listings' invalidation). No "public" is no change.
    /// <summary>Sets the legacy public flag (use the dataset metadata).</summary>
    [Obsolete("Use meta")]
    public async Task<Dictionary<string, object>> ChangeAttributes(string orgSlug, string dsSlug,
        AttributesDto? attributes, CancellationToken ct = default)
    {
        if (attributes == null)
            throw new BadRequestException("Attributes are null");

        // Write access first, also when there is nothing to change
        var access = await _access.ResolveAsync(orgSlug, dsSlug, AccessType.Write, ct: ct);

        if (attributes.IsPublic is { } isPublic)
            await Edit(orgSlug, dsSlug,
                new DatasetEditDto { Visibility = isPublic ? Visibility.Public : Visibility.Private }, ct);

        return new Dictionary<string, object>
        {
            { "public", access.Ddb.Meta.GetSafe().Visibility == Visibility.Public }
        };
    }

    private static BadRequestException SlugTakenOnRename(string slug, Exception? inner = null) =>
        new($"Dataset '{slug}' already exists", innerException: inner);

    private Task SaveUniqueSlugAsync(string orgSlug, string slug, Func<Exception, BadRequestException> taken) =>
        _creation.SaveUniqueSlugAsync(orgSlug, slug, taken);
}
