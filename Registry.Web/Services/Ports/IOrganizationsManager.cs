using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Organization CRUD interface (list, get, add, edit, delete, merge, member management).
/// </summary>
public interface IOrganizationsManager
{
    public Task<IEnumerable<OrganizationDto>> List(bool ownedOnly = false, CancellationToken ct = default);
    public Task<IEnumerable<OrganizationDto>> ListPublic(CancellationToken ct = default);
    public Task<OrganizationDto> Get(string orgSlug, CancellationToken ct = default);
    public Task<OrganizationDto> AddNew(OrganizationRequestDto organization, bool skipAuthCheck = false, CancellationToken ct = default);

    /// <summary>
    /// The personal organization of a new user (AccountOptions.EnableDefaultUserOrganization): private, named
    /// after the user, under the first free slug from the user name.
    /// </summary>
    public Task<OrganizationDto> AddUserDefaultAsync(string userId, string userName, CancellationToken ct = default);
    public Task Edit(string orgSlug, OrganizationRequestDto organization, CancellationToken ct = default);
    public Task Delete(string orgSlug, CancellationToken ct = default);

    /// <summary>
    /// Merges a source organization into a destination organization.
    /// All datasets from the source will be moved to the destination.
    /// This operation requires admin privileges.
    /// </summary>
    /// <param name="sourceOrgSlug">The source organization slug (will be merged/deleted).</param>
    /// <param name="destOrgSlug">The destination organization slug.</param>
    /// <param name="conflictResolution">How to handle conflicts with existing datasets.</param>
    /// <param name="deleteSourceOrganization">Whether to delete the source organization after merging.</param>
    /// <returns>Result of the merge operation.</returns>
    public Task<MergeOrganizationResultDto> Merge(
        string sourceOrgSlug,
        string destOrgSlug,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict,
        bool deleteSourceOrganization = true, CancellationToken ct = default);

    #region Member Management

    /// <summary>
    /// Gets all members of an organization
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <returns>List of organization members with their permissions</returns>
    Task<IEnumerable<OrganizationMemberDto>> GetMembers(string orgSlug, CancellationToken ct = default);

    /// <summary>
    /// Adds a new member to an organization
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="userName">Username to add</param>
    /// <param name="permissions">Permission level</param>
    Task AddMember(string orgSlug, string userName, OrganizationPermissions permissions = OrganizationPermissions.ReadWrite, CancellationToken ct = default);

    /// <summary>
    /// Updates a member's permission level
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="userName">Username to update</param>
    /// <param name="permissions">New permission level</param>
    Task UpdateMemberPermission(string orgSlug, string userName, OrganizationPermissions permissions, CancellationToken ct = default);

    /// <summary>
    /// Removes a member from an organization
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="userName">Username to remove</param>
    Task RemoveMember(string orgSlug, string userName, CancellationToken ct = default);

    #endregion
}