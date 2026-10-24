using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Registry.Common;
using Registry.Web.Data;
using Registry.Web.Data.Models;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Models.Configuration;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Users;

/// <summary>The local user records: created on a first login or by an administrator, synchronised with what the login provider returns.</summary>
public sealed class UserProvisioning
{
    private readonly UserManager<User> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly ApplicationDbContext _applicationDbContext;
    private readonly IPasswordPolicyValidator _passwordPolicyValidator;
    private readonly AccountOptions _accounts;
    private readonly IOrganizationsManager _organizations;
    private readonly ILogger<UserProvisioning> _logger;

    public UserProvisioning(UserManager<User> userManager, RoleManager<IdentityRole> roleManager, ApplicationDbContext applicationDbContext, IPasswordPolicyValidator passwordPolicyValidator, IOptions<AccountOptions> accounts, IOrganizationsManager organizations, ILogger<UserProvisioning> logger)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _applicationDbContext = applicationDbContext;
        _passwordPolicyValidator = passwordPolicyValidator;
        _accounts = accounts.Value;
        _organizations = organizations;
        _logger = logger;
    }

    /// <summary>
    /// Generates an internal password for users managed by an external identity provider.
    /// The password is never used for authentication (auth is always delegated to the provider),
    /// but ASP.NET Core Identity requires a hashed credential record in the local DB.
    /// The generated string satisfies any common Identity password policy:
    /// upper, lower, digit, non-alphanumeric, length >= 32.
    /// </summary>
    private static string GenerateExternalUserPassword()
        => "!" + CommonUtils.RandomString(29) + "Aa1";

    /// <summary>
    /// A password for a user of an external provider: never used to authenticate (the provider does), but Identity
    /// stores a hashed credential. It satisfies any common password policy.
    /// </summary>
    public string ExternalUserPassword() => GenerateExternalUserPassword();

    /// <summary>
    /// Stores the metadata the login provider returned for <paramref name="user"/>, saving only when it
    /// changed. A provider may return none: <see cref="User.Metadata"/> stores null as empty.
    /// </summary>
    public async Task SyncFromProviderAsync(User user, Dictionary<string, object> metadata)
    {
        await SyncMetadata(user, metadata);
        await SyncRolesAsync(user);
    }

    private async Task SyncMetadata(User user, Dictionary<string, object> metadata)
    {
        var previous = user.Metadata;
        user.Metadata = metadata;

        if (!previous.IsSameAs(user.Metadata))
            await _applicationDbContext.SaveChangesAsync();
    }

    /// <summary>Applies the roles listed in the metadata of <paramref name="user"/> (none listed: unchanged).</summary>
    public async Task SyncRolesAsync(User user)
    {
        var tmp = user.Metadata?.SafeGetValue("roles");
        var roles = tmp switch
        {
            string[] names => names,
            IEnumerable<object> values => values.OfType<string>().ToArray(),
            _ => null
        };
        if (roles == null)
            return;

        // Remove all pre-existing roles
        var userRoles = await _userManager.GetRolesAsync(user);
        foreach (var role in userRoles)
            await _userManager.RemoveFromRoleAsync(user, role);

        var edited = false;

        // Sync roles
        foreach (var role in roles)
        {
            // Create missing roles
            if (!await _roleManager.RoleExistsAsync(role))
                await _roleManager.CreateAsync(new IdentityRole { Name = role });

            // Skip if already in this role
            if (await _userManager.IsInRoleAsync(user, role)) continue;

            await _userManager.AddToRoleAsync(user, role);
            edited = true;
        }

        if (edited)
        {
            _applicationDbContext.Entry(user).State = EntityState.Modified;
            await _applicationDbContext.SaveChangesAsync();
        }
    }

    /// <summary>Creates <paramref name="user"/> (password policy, roles that must exist, the default organization when enabled).</summary>
    public async Task<User> CreateAsync(User user, string password, string[] roles = null)
    {
        await ValidateRoles(roles);

        // Validate password against policy (applies to all user creation)
        var policyResult = _passwordPolicyValidator.Validate(password);
        if (!policyResult.IsValid)
            throw new BadRequestException("Password does not meet the requirements: " +
                                          string.Join("; ", policyResult.Errors));

        var res = await _userManager.CreateAsync(user, password);

        if (!res.Succeeded)
        {
            var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
            _logger.LogWarning("Errors in creating user: {Errors}", errors);

            throw new BadRequestException("Error in creating user");
        }

        if (_accounts.EnableDefaultUserOrganization)
            await _organizations.AddUserDefaultAsync(user.Id, user.UserName);

        await AddUserToRoles(user, roles);

        return user;
    }

    private async Task AddUserToRoles(User user, string[] roles)
    {
        // Add user roles
        if (roles != null && roles.Length != 0)
        {
            foreach (var role in roles)
                await _userManager.AddToRoleAsync(user, role);
        }
    }

    private async Task ValidateRoles(string[] roles)
    {
        if (roles == null || roles.Length == 0)
            return;

        foreach (var role in roles)
        {
            if (!await _roleManager.RoleExistsAsync(role))
                throw new BadRequestException($"Role {role} does not exist");
        }
    }
}
