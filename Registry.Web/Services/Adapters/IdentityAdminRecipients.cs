#nullable enable
using System;
using System.Collections.Generic;
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
/// <see cref="IAdminRecipients"/> from the identity database: one query on the roles tables, which every host that runs
/// jobs can make because the web host and the processing nodes read the same database (the same connection string, or
/// the SQLite file in the storage folder they share). A deactivated user is not told. A host with no identity database
/// gives no recipient.
/// </summary>
public sealed class IdentityAdminRecipients : IAdminRecipients
{
    private readonly ApplicationDbContext? _identity;
    private readonly ILogger<IdentityAdminRecipients> _logger;

    /// <param name="identity">The identity database; null on a host that does not register it.</param>
    /// <param name="logger">Logger.</param>
    public IdentityAdminRecipients(ILogger<IdentityAdminRecipients> logger, ApplicationDbContext? identity = null)
    {
        _identity = identity;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EmailAddress>> GetAsync(CancellationToken ct)
    {
        if (_identity is null)
        {
            _logger.LogDebug("This host has no identity database: the administrators cannot be told");
            return [];
        }

        var admin = ApplicationDbContext.AdminRoleName.ToUpperInvariant();
        var deactivated = ApplicationDbContext.DeactivatedRoleName.ToUpperInvariant();

        var addresses = await (
                from user in _identity.Users
                join userRole in _identity.UserRoles on user.Id equals userRole.UserId
                join role in _identity.Roles on userRole.RoleId equals role.Id
                where role.NormalizedName == admin && user.Email != null &&
                      !(from other in _identity.UserRoles
                        join otherRole in _identity.Roles on other.RoleId equals otherRole.Id
                        where other.UserId == user.Id && otherRole.NormalizedName == deactivated
                        select other).Any()
                orderby user.Email
                select user.Email!)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // One bare mailbox each: the e-mail service refuses a message with a malformed recipient, which would stop all of them
        return addresses
            .Select(a => a.Trim())
            .Where(EmailAddress.IsValid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => new EmailAddress(a))
            .ToList();
    }
}
