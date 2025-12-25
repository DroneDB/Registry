using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Options;
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
}