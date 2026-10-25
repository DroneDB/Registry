#nullable enable
using MimeKit;

namespace Registry.Web.Models.Email;

/// <summary>A recipient or sender: the bare address and an optional display name.</summary>
/// <param name="Address">The address, e.g. <c>jane@example.com</c>.</param>
/// <param name="DisplayName">The name shown next to it.</param>
public sealed record EmailAddress(string Address, string? DisplayName = null)
{
    /// <summary>
    /// Whether <paramref name="address"/> is one bare mailbox with a domain (<c>jane@example.com</c>): not a
    /// list, not a <c>Name &lt;address&gt;</c> form, not a local-only name.
    /// </summary>
    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var trimmed = address.Trim();
        return MailboxAddress.TryParse(ParserOptions.Default, trimmed, out var mailbox) &&
               string.IsNullOrEmpty(mailbox.Name) &&
               !string.IsNullOrEmpty(mailbox.Domain) &&
               mailbox.Address == trimmed;
    }
}
