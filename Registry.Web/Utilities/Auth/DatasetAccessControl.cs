using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Registry.Common;
using Registry.Ports;
using Registry.Ports.DroneDB;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Services.Managers;

namespace Registry.Web.Utilities.Auth;

/// <summary>
/// Handles dataset-specific access control
/// </summary>
public class DatasetAccessControl : AccessControlBase
{
    private readonly IDdbManager _ddbManager;
    private readonly ICacheManager _cacheManager;

    public DatasetAccessControl(UserManager<User> usersManager, RegistryContext context, ILogger logger, IDdbManager ddbManager, ICacheManager cacheManager)
        : base(usersManager, context, logger)
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

        var visibilityBytes = await _cacheManager.GetAsync(
            MagicStrings.DatasetVisibilityCacheSeed,
            org.Slug,
            org.Slug,
            dataset.InternalRef,
            _ddbManager
        );

        if (visibilityBytes.Length < sizeof(int))
        {
            Logger.LogWarning("Invalid visibility cache data for dataset {OrgSlug}/{DatasetSlug}, defaulting to Private",
                org.Slug, dataset.Slug);
            return Visibility.Private;
        }

        return (Visibility)BitConverter.ToInt32(visibilityBytes, 0);
    }

    public async Task<bool> CanAccessDataset(Dataset dataset, AccessType access, User user)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        var org = dataset.Organization;

        // Check dataset visibility from cache
        var visibility = await GetDatasetVisibilityFromCache(dataset);
        var isPublicOrUnlisted = visibility is Visibility.Public or Visibility.Unlisted;

        Logger.LogDebug("Dataset {OrgSlug}/{DatasetSlug} visibility from cache: {Visibility}",
            org.Slug, dataset.Slug, visibility);

        // Anonymous access check
        if (user == null)
        {
            // Anonymous users can access only public or unlisted datasets with Read access
            if (access != AccessType.Read || !isPublicOrUnlisted) return false;

            var owner = await UsersManager.FindByIdAsync(org.OwnerId);
            // Check if the owner is not deactivated
            return owner != null && !await IsUserDeactivated(owner);
        }

        // Deactivated user check
        if (await IsUserDeactivated(user))
            return false;

        // Admin privileges
        var isAdmin = await IsUserAdmin(user);
        if (isAdmin)
        {
            // Admin can access any dataset and perform any action
            return true;
        }

        // Owner privileges
        if (org.OwnerId == user.Id)
        {
            // Owner can access and modify datasets
            return true;
        }

        if (org.Users == null)
            await _context.Entry(org).Collection(o => o.Users).LoadAsync();

        // Organization member access
        var orgUser = org.Users?.FirstOrDefault(u => u.UserId == user.Id);
        if (orgUser == null)
        {
            // Non-organization members can only read public or unlisted datasets
            return access == AccessType.Read && isPublicOrUnlisted;
        }

        var orgUserDetails = await UsersManager.FindByIdAsync(orgUser.UserId);
        if (orgUserDetails == null)
        {
            Logger.LogInformation("User {UserId} not found for organization {OrganizationSlug}",
                orgUser.UserId, org.Slug);
            return false;
        }

        // Check if user is deactivated
        if (await IsUserDeactivated(orgUserDetails))
            return false;

        // Check permission level against requested access
        return orgUser.Permissions.HasAccess(access);
    }
}
