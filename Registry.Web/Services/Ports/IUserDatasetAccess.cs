using System.Threading;
using System.Threading.Tasks;

namespace Registry.Web.Services.Ports;

/// <summary>
/// Whether a given user, not the caller of a request, may read a dataset now: the rules the API applies to a signed-in
/// user (visibility, ownership, membership, the admin and deactivated roles), for work done outside a request, such as
/// an e-mail about the dataset.
/// </summary>
public interface IUserDatasetAccess
{
    /// <summary>
    /// Whether <paramref name="userId"/> may read <paramref name="orgSlug"/>/<paramref name="dsSlug"/>; false when the
    /// dataset or the user does not exist, or the host cannot tell (no identity database).
    /// </summary>
    Task<bool> CanReadAsync(string userId, string orgSlug, string dsSlug, CancellationToken ct);
}
