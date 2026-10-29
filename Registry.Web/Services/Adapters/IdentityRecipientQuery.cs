using System.Linq;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Email;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Who may get an e-mail, read straight from the identity database: the processing nodes have no
/// <c>UserManager</c>, only the <see cref="ApplicationDbContext"/> they share with the web host. One rule for every
/// recipient lookup: a deactivated user is never written to, and an address that is not one bare mailbox is skipped
/// (the e-mail service refuses a message with a malformed recipient, which would stop the others too).
/// </summary>
internal static class IdentityRecipientQuery
{
    /// <summary>The users that are not deactivated and have an address (not yet checked, see <see cref="Mailbox(string?)"/>).</summary>
    public static IQueryable<User> ActiveWithEmail(ApplicationDbContext identity)
    {
        var deactivated = IdentityRoleQuery.UserIdsIn(identity, ApplicationDbContext.DeactivatedRoleName);

        return identity.Users.Where(user => user.Email != null && !deactivated.Contains(user.Id));
    }

    /// <summary>The address as a recipient, or null when it is not one bare mailbox.</summary>
    public static EmailAddress? Mailbox(string? address) => Mailbox(address, null);

    /// <summary>The address as a recipient named <paramref name="displayName"/>, or null when it is not one bare mailbox.</summary>
    public static EmailAddress? Mailbox(string? address, string? displayName) =>
        EmailAddress.IsValid(address)
            ? new EmailAddress(address!.Trim(), string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim())
            : null;
}
