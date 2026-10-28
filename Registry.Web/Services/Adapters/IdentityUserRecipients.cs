#nullable enable
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Identity;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// <see cref="IUserRecipients"/> from the identity database, with the rules of <see cref="IdentityRecipientQuery"/>
/// (the same as <see cref="IdentityAdminRecipients"/>). A host with no identity database finds nobody.
/// </summary>
public sealed class IdentityUserRecipients : IUserRecipients
{
    private readonly ApplicationDbContext? _identity;
    private readonly ILogger<IdentityUserRecipients> _logger;

    /// <param name="logger">Logger.</param>
    /// <param name="identity">The identity database; null on a host that does not register it.</param>
    public IdentityUserRecipients(ILogger<IdentityUserRecipients> logger, ApplicationDbContext? identity = null)
    {
        _identity = identity;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EmailAddress?> FindAsync(string? userId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(userId))
            return null;

        if (_identity is null)
        {
            _logger.LogDebug("This host has no identity database: user {UserId} cannot be written to", userId);
            return null;
        }

        var found = await IdentityRecipientQuery.ActiveWithEmail(_identity)
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new { user.Email, user.UserName })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // The user name rides along as the display name: what the e-mail calls the user, with no second query
        return found == null ? null : IdentityRecipientQuery.Mailbox(found.Email, found.UserName);
    }
}
