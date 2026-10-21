#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Data.Models;
using Registry.Web.Identity.Models;

namespace Registry.Web.Services.Ports;

/// <summary>
/// The one implementation of organization membership rows (RQ-06), used by the organization member
/// routes (<c>/orgs/{orgSlug}/members</c>) and the administrator routes (<c>/users/{userName}/orgs</c>).
/// It does not authorize: each caller checks who may change the members first. Every grant records
/// <see cref="OrganizationUser.GrantedAt"/> and <see cref="OrganizationUser.GrantedBy"/>.
/// <para>Codes: an unknown organization or user 404, already a member 409, the organization's owner as
/// the target 400, not a member 404.</para>
/// </summary>
public interface IOrganizationMembershipService
{
    /// <summary>The organization <paramref name="orgSlug"/> with its members, tracked.</summary>
    /// <exception cref="Exceptions.NotFoundException">There is no such organization.</exception>
    Task<Organization> GetWithMembersAsync(string orgSlug, CancellationToken ct = default);

    /// <summary>Makes <paramref name="userName"/> a member of <paramref name="organization"/> (loaded by
    /// <see cref="GetWithMembersAsync"/>) with <paramref name="permissions"/>, granted by <paramref name="grantedBy"/>.</summary>
    Task AddAsync(Organization organization, string userName, OrganizationPermissions permissions, User grantedBy,
        CancellationToken ct = default);

    /// <summary>Changes the permissions of the member <paramref name="userName"/>; the change is a new grant
    /// by <paramref name="grantedBy"/>.</summary>
    Task UpdateAsync(Organization organization, string userName, OrganizationPermissions permissions, User grantedBy,
        CancellationToken ct = default);

    /// <summary>Removes the member <paramref name="userName"/>.</summary>
    Task RemoveAsync(Organization organization, string userName, User removedBy, CancellationToken ct = default);
}
