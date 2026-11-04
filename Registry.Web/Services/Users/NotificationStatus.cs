using System.Threading;
using System.Threading.Tasks;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Users;

/// <summary>
/// Whether the signed-in user can get notification e-mails, and why not: the Hub greys out the setting with the reason,
/// because a user cannot see or change their own address there.
/// </summary>
public sealed class NotificationStatus
{
    private readonly IEmailService _email;
    private readonly IAuthManager _auth;
    private readonly IUserRecipients _recipients;

    public NotificationStatus(IEmailService email, IAuthManager auth, IUserRecipients recipients)
    {
        _email = email;
        _auth = auth;
        _recipients = recipients;
    }

    /// <summary>The status of the signed-in user.</summary>
    public async Task<NotificationStatusDto> GetAsync(CancellationToken ct)
    {
        if (!_email.IsEnabled)
            return new NotificationStatusDto { CanReceiveEmail = false, Reason = NotificationStatusDto.EmailDisabled };

        // The same lookup the e-mails use, so "can receive" means an e-mail would actually be addressed
        var address = await _recipients.FindAsync(await _auth.GetCurrentUserId(), ct);

        return address == null
            ? new NotificationStatusDto { CanReceiveEmail = false, Reason = NotificationStatusDto.NoAddress }
            : new NotificationStatusDto { CanReceiveEmail = true };
    }
}
