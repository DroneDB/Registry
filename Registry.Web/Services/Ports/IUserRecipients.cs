#nullable enable
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.Email;

namespace Registry.Web.Services.Ports;

/// <summary>Where an e-mail to one user goes, on any host that runs jobs or serves requests.</summary>
public interface IUserRecipients
{
    /// <summary>
    /// The address of the user <paramref name="userId"/>, with the user name as its <see cref="EmailAddress.DisplayName"/>;
    /// null when the user does not exist, is deactivated, has no address or one that is not a bare mailbox, or when this
    /// host cannot read the users.
    /// </summary>
    /// <param name="userId">The user id (<c>AspNetUsers.Id</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<EmailAddress?> FindAsync(string userId, CancellationToken ct);
}
