#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.Email;

namespace Registry.Web.Services.Ports;

/// <summary>Who the administrators of this deployment are, as far as an e-mail to them is concerned.</summary>
public interface IAdminRecipients
{
    /// <summary>
    /// The users in the admin role that have an e-mail address, each address once. Empty when there are none, or when this
    /// host cannot read the users.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<EmailAddress>> GetAsync(CancellationToken ct);
}
