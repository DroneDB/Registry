using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Services.Managers;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Whether a given user, not the caller of a request, may read a dataset now: the rules the API applies to a signed-in
/// user (visibility, ownership, membership, the admin and deactivated roles), for work done outside a request, such as
/// an e-mail about the dataset or a task that acts for the user who requested it.
/// </summary>
public interface IUserDatasetAccess
{
    /// <summary>
    /// Whether <paramref name="userId"/> may read <paramref name="orgSlug"/>/<paramref name="dsSlug"/>; false when the
    /// dataset or the user does not exist, or the host cannot tell (no identity database).
    /// </summary>
    Task<bool> CanReadAsync(string userId, string orgSlug, string dsSlug, CancellationToken ct);

    /// <summary>
    /// Whether <paramref name="userId"/> has <paramref name="access"/> to the organization <paramref name="orgSlug"/>,
    /// by the rules of the API (<c>IDatasetAccessResolver.ResolveOrganizationAsync</c>); false when the organization or
    /// the user does not exist, or the host cannot tell (no identity database).
    /// </summary>
    Task<bool> CanAccessOrganizationAsync(string userId, string orgSlug, AccessType access, CancellationToken ct);
}
