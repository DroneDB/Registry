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
/// the SQLite file in the storage folder they share), with the recipient rules of <see cref="IdentityRecipientQuery"/>. A
/// host with no identity database gives no recipient.
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

        var addresses = await (
                from user in IdentityRecipientQuery.ActiveWithEmail(_identity)
                join userRole in _identity.UserRoles on user.Id equals userRole.UserId
                join role in _identity.Roles on userRole.RoleId equals role.Id
                where role.NormalizedName == admin
                orderby user.Email
                select user.Email!)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // One mailbox each, and each once
        return addresses
            .Select(address => IdentityRecipientQuery.Mailbox(address))
            .OfType<EmailAddress>()
            .DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
