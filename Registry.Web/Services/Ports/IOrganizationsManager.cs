using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Models;
using Registry.Web.Models.DTO;

namespace Registry.Web.Services.Ports;

public interface IOrganizationsManager
{
    public Task<IEnumerable<OrganizationDto>> List();
    public Task<IEnumerable<OrganizationDto>> ListPublic();
    public Task<OrganizationDto> Get(string orgSlug);
    public Task<OrganizationDto> AddNew(OrganizationDto organization, bool skipAuthCheck = false);
    public Task Edit(string orgSlug, OrganizationDto organization);
    public Task Delete(string orgSlug);

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
        bool deleteSourceOrganization = true);

    #region Member Management

    /// <summary>
    /// Gets all members of an organization
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <returns>List of organization members with their permissions</returns>
    Task<IEnumerable<OrganizationMemberDto>> GetMembers(string orgSlug);

    /// <summary>
    /// Adds a new member to an organization
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="userName">Username to add</param>
    /// <param name="permissions">Permission level</param>
    Task AddMember(string orgSlug, string userName, OrganizationPermissions permissions = OrganizationPermissions.ReadWrite);

    /// <summary>
    /// Updates a member's permission level
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="userName">Username to update</param>
    /// <param name="permissions">New permission level</param>
    Task UpdateMemberPermission(string orgSlug, string userName, OrganizationPermissions permissions);

    /// <summary>
    /// Removes a member from an organization
    /// </summary>
    /// <param name="orgSlug">Organization slug</param>
    /// <param name="userName">Username to remove</param>
    Task RemoveMember(string orgSlug, string userName);

    /// <summary>
    /// Checks if organization member management feature is enabled
    /// </summary>
    bool IsMemberManagementEnabled { get; }

    #endregion
}