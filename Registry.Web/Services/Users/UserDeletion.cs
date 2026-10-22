using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Users;

/// <summary>The deletion of a user, with their data deleted or transferred to a successor; a user of an external provider is deactivated instead.</summary>
public interface IUserDeletion
{
    Task<DeleteUserResultDto> DeleteUser(string userName, string successor = null,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.Rename);
}

/// <summary>Default <see cref="IUserDeletion"/> (RQ-12, out of <c>UsersManager</c>).</summary>
public sealed class UserDeletion : IUserDeletion
{
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly IOrganizationsManager _organizationsManager;
    private readonly RegistryContext _registryContext;
    private readonly IdentityProviderOptions _identity;
    private readonly ILogger<UserDeletion> _logger;

    public UserDeletion(ILoginManager loginManager, UserManager<User> userManager, IOrganizationsManager organizationsManager, RegistryContext registryContext, IOptions<IdentityProviderOptions> identity, ILogger<UserDeletion> logger)
    {
        _loginManager = loginManager;
        _userManager = userManager;
        _organizationsManager = organizationsManager;
        _registryContext = registryContext;
        _identity = identity.Value;
        _logger = logger;
    }

    /// <summary>Shorthand accessor for the active provider's capability descriptor.</summary>
    private AuthProviderCapabilities Caps => _loginManager.Capabilities;

    public async Task<DeleteUserResultDto> DeleteUser(
        string userName,
        string successor = null,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.Rename)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new BadRequestException("userName should not be empty");

        if (userName == MagicStrings.AnonymousUserName)
            throw new ForbiddenException("Cannot delete the anonymous user");

        if (userName == _identity.DefaultAdmin.UserName)
            throw new ForbiddenException("Cannot delete the default admin");

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("User does not exist");

        // For external providers, physically deleting a user is futile (the user would be re-created
        // on the next login if still valid in the directory). Deactivate instead.
        if (!Caps.SupportsLocalUserManagement)
        {
            // Deactivation only flags the account: no data transfer or resource cleanup is performed.
            // Fail fast if a successor was requested so the caller is not misled into believing a
            // transfer occurred.
            if (!string.IsNullOrWhiteSpace(successor))
                throw new BadRequestException(
                    "Transferring data to a successor is not supported when an external identity " +
                    "provider is active: the account is deactivated, not deleted.");

            _logger.LogWarning(
                "User {UserName} is managed by an external identity provider. " +
                "Deactivating instead of deleting", userName);

            if (!await _userManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName))
                await _userManager.AddToRoleAsync(user, ApplicationDbContext.DeactivatedRoleName);

            return new DeleteUserResultDto { UserName = userName, DatasetResults = [] };
        }

        // Validate successor if specified
        User successorUser = null;
        if (!string.IsNullOrWhiteSpace(successor))
        {
            if (successor == userName)
                throw new BadRequestException("Successor cannot be the same as the user being deleted");

            successorUser = await _userManager.FindByNameAsync(successor);
            if (successorUser == null)
                throw new BadRequestException($"Successor user '{successor}' does not exist");
        }

        var result = new DeleteUserResultDto
        {
            UserName = userName,
            Successor = successor,
            DatasetResults = []
        };

        // 1. Delete all batches for this user
        var userBatches = await _registryContext.Batches
            .Where(b => b.UserName == userName)
            .ToListAsync();

        result.BatchesDeleted = userBatches.Count;
        _registryContext.Batches.RemoveRange(userBatches);
        await _registryContext.SaveChangesAsync();

        _logger.LogInformation("Deleted {Count} batches for user {UserName}", userBatches.Count, userName);

        // 2. Get all organizations owned by this user
        var userOrgs = await _registryContext.Organizations
            .Include(o => o.Datasets)
            .Include(o => o.Users)
            .Where(o => o.OwnerId == user.Id)
            .ToListAsync();

        if (successorUser != null)
        {
            // Transfer data to successor
            var allDatasetResults = new List<MoveDatasetResultDto>();

            // Get successor's organizations to check for conflicts
            var successorOrgs = await _registryContext.Organizations
                .Include(o => o.Datasets)
                .Where(o => o.OwnerId == successorUser.Id)
                .ToDictionaryAsync(o => o.Slug, o => o);

            foreach (var org in userOrgs)
            {
                // Check if successor has an organization with the same slug
                if (successorOrgs.TryGetValue(org.Slug, out var existingOrg))
                {
                    // Merge datasets into successor's existing organization
                    if (org.Datasets.Count != 0)
                    {
                        var mergeResult = await _organizationsManager.Merge(
                            org.Slug,
                            existingOrg.Slug,
                            conflictResolution,
                            deleteSourceOrganization: true);

                        allDatasetResults.AddRange(mergeResult.DatasetResults);
                        result.DatasetsTransferred += mergeResult.DatasetsMovedCount;
                    }
                    else
                    {
                        // No datasets, just delete the empty organization
                        await _organizationsManager.Delete(org.Slug);
                    }

                    result.OrganizationsDeleted++;
                }
                else
                {
                    // Transfer ownership of the organization to successor
                    org.OwnerId = successorUser.Id;
                    result.OrganizationsTransferred++;
                    result.DatasetsTransferred += org.Datasets.Count;

                    _logger.LogInformation("Transferred organization {OrgSlug} to successor {Successor}",
                        org.Slug, successor);
                }
            }

            result.DatasetResults = [.. allDatasetResults];
            await _registryContext.SaveChangesAsync();
        }
        else
        {
            // Delete all organizations (cascades to datasets)
            foreach (var org in userOrgs)
            {
                result.DatasetsDeleted += org.Datasets.Count;
                await _organizationsManager.Delete(org.Slug);
                result.OrganizationsDeleted++;

                _logger.LogInformation("Deleted organization {OrgSlug} for user {UserName}", org.Slug, userName);
            }
        }

        // 3. Remove user from all organizations (membership)
        var memberships = await _registryContext.OrganizationsUsers
            .Where(ou => ou.UserId == user.Id)
            .ToListAsync();

        _registryContext.OrganizationsUsers.RemoveRange(memberships);
        await _registryContext.SaveChangesAsync();

        _logger.LogInformation("Removed {Count} organization memberships for user {UserName}",
            memberships.Count, userName);

        // 4. Delete the user from Identity
        var identityResult = await _userManager.DeleteAsync(user);

        if (!identityResult.Succeeded)
        {
            var errors = string.Join(";", identityResult.Errors.Select(item => $"{item.Code} - {item.Description}"));
            _logger.LogWarning("Errors in deleting user: {Errors}", errors);
            throw new ConflictException("Cannot delete user: " + errors);
        }

        _logger.LogInformation("Successfully deleted user {UserName}{Successor}",
            userName,
            successorUser != null ? $" with data transferred to {successor}" : "");

        return result;
    }
}
