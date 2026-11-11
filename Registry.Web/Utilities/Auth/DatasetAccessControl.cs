using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Handles dataset-specific access control
/// </summary>
public class DatasetAccessControl : AccessControlBase
{
    private readonly IDdbManager _ddbManager;
    private readonly ICacheManager _cacheManager;

    public DatasetAccessControl(IUserAuthSnapshotProvider snapshots, RegistryContext context, ILogger logger, IDdbManager ddbManager, ICacheManager cacheManager)
        : base(snapshots, context, logger)
    {
        _ddbManager = ddbManager;
        _cacheManager = cacheManager;
    }

    /// <summary>
    /// Gets dataset visibility from cache
    /// </summary>
    /// <param name="dataset">The dataset to get visibility for</param>
    /// <returns>The visibility value from cache</returns>
    private async Task<Visibility> GetDatasetVisibilityFromCache(Dataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var org = dataset.Organization;

        var visibility = await _cacheManager.GetDatasetVisibilityAsync(org.Slug, dataset.InternalRef, _ddbManager);

        if (visibility == null)
        {
            Logger.LogWarning("Invalid visibility cache data for dataset {OrgSlug}/{DatasetSlug}, defaulting to Private",
                org.Slug, dataset.Slug);
            return Visibility.Private;
        }

        Logger.LogDebug("Dataset {OrgSlug}/{DatasetSlug} visibility from cache: {Visibility}",
            org.Slug, dataset.Slug, visibility.Value);

        return visibility.Value;
    }

    /// <param name="userId">Id of an existing user, or null for anonymous access</param>
    public async Task<bool> CanAccessDataset(Dataset dataset, AccessType access, string? userId) =>
        await CanAccess(dataset, await GetDatasetVisibilityFromCache(dataset), access, userId);

    /// <summary>
    /// Whether a listing of datasets (the datasets of an organization, their map, the STAC catalog) shows
    /// <paramref name="dataset"/> to the user. Anyone with its link reads an unlisted dataset, so a listing that
    /// showed it to everyone who can read it would hand that link out: an unlisted dataset is listed as a private
    /// one is, to the owner of its organization, its members and the administrators only. The other datasets are
    /// listed to whoever can read them.
    /// </summary>
    /// <param name="userId">Id of an existing user, or null for anonymous access</param>
    public async Task<bool> CanListDataset(Dataset dataset, string? userId)
    {
        var visibility = await GetDatasetVisibilityFromCache(dataset);
        return await CanAccess(dataset, visibility == Visibility.Unlisted ? Visibility.Private : visibility,
            AccessType.Read, userId);
    }

    private async Task<bool> CanAccess(Dataset dataset, Visibility visibility, AccessType access, string? userId)
    {
        var org = dataset.Organization;
        var isPublicOrUnlisted = visibility is Visibility.Public or Visibility.Unlisted;

        // Anonymous access check
        if (userId == null)
        {
            // Anonymous users can access only public or unlisted datasets with Read access,
            // and only while the owner is not deactivated
            if (access != AccessType.Read || !isPublicOrUnlisted) return false;

            return await IsOwnerActive(org.OwnerId);
        }

        var user = await Snapshots.GetAsync(userId);

        // Deactivated user check
        if (user.IsDeactivated)
            return false;

        // Admin privileges
        if (user.IsAdmin)
        {
            // Admin can access any dataset and perform any action
            return true;
        }

        // Owner privileges
        if (org.OwnerId == userId)
        {
            // Owner can access and modify datasets
            return true;
        }

        if (org.Users == null)
            await Context.Entry(org).Collection(o => o.Users).LoadAsync();

        // Organization member access
        var orgUser = org.Users?.FirstOrDefault(u => u.UserId == userId);
        if (orgUser == null)
        {
            // Non-organization members can only read public or unlisted datasets
            return access == AccessType.Read && isPublicOrUnlisted;
        }

        // Check permission level against requested access
        return orgUser.Permissions.HasAccess(access);
    }
}
