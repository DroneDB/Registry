using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Registry.Web.Identity.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Users;

/// <summary>
/// The organization a new user gets: their own default organization, named after them, when
/// <see cref="AccountOptions.EnableDefaultUserOrganization"/> is on (<see cref="UserProvisioning.CreateAsync"/>).
/// </summary>
public sealed class NewUserOrganization
{
    private readonly AccountOptions _accounts;
    private readonly IOrganizationsManager _organizations;

    public NewUserOrganization(IOptions<AccountOptions> accounts, IOrganizationsManager organizations)
    {
        _accounts = (accounts ?? throw new ArgumentNullException(nameof(accounts))).Value;
        _organizations = organizations ?? throw new ArgumentNullException(nameof(organizations));
    }

    /// <summary>Adds the default organization of <paramref name="user"/>, just created, when new users get one.</summary>
    public async Task AddForAsync(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (_accounts.EnableDefaultUserOrganization)
            await _organizations.AddUserDefaultAsync(user.Id, user.RequiredUserName());
    }
}
