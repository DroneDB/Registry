using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
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

        return visibility.Value;
    }

    public Task<bool> CanAccessDataset(Dataset dataset, AccessType access, User user)
        => CanAccessDataset(dataset, access, user?.Id);

    /// <param name="userId">Id of an existing user, or null for anonymous access</param>
    public async Task<bool> CanAccessDataset(Dataset dataset, AccessType access, string userId)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var org = dataset.Organization;

        // Check dataset visibility from cache
        var visibility = await GetDatasetVisibilityFromCache(dataset);
        var isPublicOrUnlisted = visibility is Visibility.Public or Visibility.Unlisted;

        Logger.LogDebug("Dataset {OrgSlug}/{DatasetSlug} visibility from cache: {Visibility}",
            org.Slug, dataset.Slug, visibility);

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
            await _context.Entry(org).Collection(o => o.Users).LoadAsync();

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
