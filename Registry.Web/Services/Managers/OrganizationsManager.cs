using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Organizations;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// The organizations API (<see cref="IOrganizationsManager"/>, what the controllers and the sign-up call) over the
/// services RQ-12 split it into (9 parameters before): reading, creating and editing, merging, and the members.
/// </summary>
public class OrganizationsManager : IOrganizationsManager
{
    private readonly IOrganizationQueries _queries;
    private readonly IOrganizationCommands _commands;
    private readonly IOrganizationMerger _merger;
    private readonly IOrganizationMembers _members;

    public OrganizationsManager(IOrganizationQueries queries, IOrganizationCommands commands,
        IOrganizationMerger merger, IOrganizationMembers members)
    {
        _queries = queries;
        _commands = commands;
        _merger = merger;
        _members = members;
    }

    public Task<IEnumerable<OrganizationDto>> List(bool ownedOnly = false, CancellationToken ct = default) =>
        _queries.List(ownedOnly, ct);

    public Task<IEnumerable<OrganizationDto>> ListPublic(CancellationToken ct = default) => _queries.ListPublic(ct);

    public Task<OrganizationDto> Get(string orgSlug, CancellationToken ct = default) => _queries.GetOrganization(orgSlug, ct);

    public Task<OrganizationDto> AddUserDefaultAsync(string userId, string userName, CancellationToken ct = default) =>
        _commands.AddUserDefaultAsync(userId, userName, ct);

    public Task<OrganizationDto> AddNew(OrganizationRequestDto organization, bool skipAuthCheck = false,
        CancellationToken ct = default) => _commands.AddNew(organization, skipAuthCheck, ct);

    public Task Edit(string orgSlug, OrganizationRequestDto organization, CancellationToken ct = default) =>
        _commands.Edit(orgSlug, organization, ct);

    public Task Delete(string orgSlug, CancellationToken ct = default) => _commands.Delete(orgSlug, ct);

    public Task<MergeOrganizationResultDto> Merge(string sourceOrgSlug, string destOrgSlug,
        ConflictResolutionStrategy conflictResolution = ConflictResolutionStrategy.HaltOnConflict,
        bool deleteSourceOrganization = true, CancellationToken ct = default) =>
        _merger.Merge(sourceOrgSlug, destOrgSlug, conflictResolution, deleteSourceOrganization, ct);

    public bool IsMemberManagementEnabled => _members.IsMemberManagementEnabled;

    public Task<IEnumerable<OrganizationMemberDto>> GetMembers(string orgSlug, CancellationToken ct = default) =>
        _members.GetMembers(orgSlug, ct);

    public Task AddMember(string orgSlug, string userName, OrganizationPermissions permissions = OrganizationPermissions.ReadWrite,
        CancellationToken ct = default) => _members.AddMember(orgSlug, userName, permissions, ct);

    public Task UpdateMemberPermission(string orgSlug, string userName, OrganizationPermissions permission,
        CancellationToken ct = default) => _members.UpdateMemberPermission(orgSlug, userName, permission, ct);

    public Task RemoveMember(string orgSlug, string userName, CancellationToken ct = default) =>
        _members.RemoveMember(orgSlug, userName, ct);
}
