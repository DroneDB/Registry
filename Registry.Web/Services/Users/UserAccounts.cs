using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Data;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Models.Email;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.Services.Users;

/// <summary>The accounts an administrator manages: creation, profile, metadata.</summary>
public sealed class UserAccounts
{
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly UserProvisioning _provisioning;
    private readonly IAuthManager _authManager;
    private readonly ApplicationDbContext _applicationDbContext;
    private readonly RegistryContext _registryContext;
    private readonly ILogger<UserAccounts> _logger;

    public UserAccounts(ILoginManager loginManager, UserManager<User> userManager, UserProvisioning provisioning, IAuthManager authManager, ApplicationDbContext applicationDbContext, RegistryContext registryContext, ILogger<UserAccounts> logger)
    {
        _loginManager = loginManager;
        _userManager = userManager;
        _provisioning = provisioning;
        _authManager = authManager;
        _applicationDbContext = applicationDbContext;
        _registryContext = registryContext;
        _logger = logger;
    }

    /// <summary>Shorthand accessor for the active provider's capability descriptor.</summary>
    private AuthProviderCapabilities Caps => _loginManager.Capabilities;

    public async Task<UserDto> CreateUser(string userName, string? email, string password, string[]? roles)
    {
        if (!Caps.SupportsLocalUserManagement)
            throw new ForbiddenException(
                "Manual user creation is disabled. Users are provisioned automatically on their first login " +
                "via the configured identity provider.", noRetry: true);

        if (!IsValidUserName(userName))
            throw new BadRequestException("The provided username is not valid");

        // The e-mail address is optional; one that is given must be one valid address
        email = string.IsNullOrWhiteSpace(email) ? null : CheckedEmail(email);

        var user = await _userManager.FindByNameAsync(userName);

        if (user != null)
            throw new ConflictException("User already exists");

        user = await _provisioning.CreateAsync(new User { UserName = userName, Email = email }, password, roles);

        _logger.LogInformation("User {UserName} created successfully", user.UserName);

        return new UserDto
        {
            UserName = user.UserName,
            Email = user.Email,
            Roles = roles ?? [],
            Organizations =
            [
                .. _registryContext.Organizations
                    .AsNoTracking()
                    .Where(org => org.OwnerId == user.Id || org.Users.Any(item => item.UserId == user.Id))
                    .Select(org => org.Slug)
            ]
        };
    }

    private static bool IsValidUserName(string userName)
    {
        return Regex.IsMatch(userName, "^[a-z0-9]{1,127}$", RegexOptions.Singleline);
    }

    // The address as stored (trimmed), or the 400 of the e-mail rule
    private static string CheckedEmail(string email) =>
        EmailAddress.IsValid(email) ? email.Trim() : throw new BadRequestException(InputRules.InvalidEmailMessage);

    public async Task UpdateUser(string userName, string? email)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new BadRequestException("User name cannot be empty");

        // If the user is not an admin, they can only update their own data
        if (string.IsNullOrWhiteSpace(email))
            throw new BadRequestException("Email cannot be empty");

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (Caps.ManagesProfileExternally)
            throw new ForbiddenException(
                "Profile data (email) is synchronised from the identity provider and cannot be modified locally.", noRetry: true);

        // Checked only when it changes: an address stored before the rule does not block an edit of the roles
        if (!string.Equals(email.Trim(), user.Email?.Trim(), StringComparison.Ordinal))
            user.Email = CheckedEmail(email);

        _applicationDbContext.Entry(user).State = EntityState.Modified;
        await _applicationDbContext.SaveChangesAsync();
    }

    public async Task<Dictionary<string, object>> GetUserMeta(string? userName = null)
    {
        var user = await _authManager.GetCurrentUser();

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (string.IsNullOrWhiteSpace(userName))
            return user.Metadata;

        user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        return user.Metadata;
    }

    public async Task SetUserMeta(string userName, Dictionary<string, object> meta)
    {
        var user = await _authManager.GetCurrentUser();

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (string.IsNullOrWhiteSpace(userName))
            throw new BadRequestException("User name cannot be empty");

        user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("Cannot find user " + userName);

        // The roles of the metadata are applied: they cannot disable the user (D-043)
        await _provisioning.RefuseDeactivatedAsync(user, UserProvisioning.RequestedMetadataRoles(meta));

        user.Metadata = meta;

        await _provisioning.SyncRolesAsync(user);

        await _applicationDbContext.SaveChangesAsync();
    }
}
