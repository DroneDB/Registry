using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

        // The rule of FR-XC-12 for users created here (D-007); a user an external provider signs in keeps the Identity set
        if (!NameRules.IsValid(userName))
            throw new InvalidFieldException(UserNameField, NameRules.InvalidMessage("User name"));

        // The e-mail address is optional; one that is given must be one valid address
        email = string.IsNullOrWhiteSpace(email) ? null : CheckedEmail(email);

        var user = await _userManager.FindByNameAsync(userName);

        if (user != null)
            throw new ConflictException("User already exists")
            {
                FieldErrors = new Dictionary<string, string[]> { [UserNameField] = ["User already exists"] }
            };

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

    // The members of the requests that name a field (D-047)
    private const string UserNameField = "userName";
    private const string EmailField = "email";

    // The address as stored (trimmed), or the 400 of the e-mail rule at the e-mail field
    private static string CheckedEmail(string email) =>
        EmailAddress.IsValid(email) ? email.Trim() : throw new InvalidFieldException(EmailField, InputRules.InvalidEmailMessage);

    /// <summary>
    /// Edit User, the address: a null <paramref name="email"/> keeps the current one; an empty or blank one removes it
    /// (the address is optional, as in <see cref="CreateUser"/>); any other must be one valid address (FR-ADM-07, FR-XC-12).
    /// When the identity provider manages the profile, only a change of the address is refused (403): an omitted or
    /// unchanged one is accepted, so the roles of the user can be edited.
    /// </summary>
    public async Task UpdateUser(string userName, string? email)
    {
        if (string.IsNullOrWhiteSpace(userName))
            throw new BadRequestException("User name cannot be empty");

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("User does not exist");

        if (email != null)
        {
            // Checked only when it changes: an address stored before the rule does not block an edit of the roles
            var wanted = email.Trim();
            if (!string.Equals(wanted, user.Email?.Trim() ?? "", StringComparison.Ordinal))
            {
                // An omitted or unchanged address leaves the profile alone, so the roles can still be edited
                if (Caps.ManagesProfileExternally)
                    throw new ForbiddenException(
                        "Profile data (email) is synchronised from the identity provider and cannot be modified locally.", noRetry: true);

                user.Email = wanted.Length == 0 ? null : CheckedEmail(wanted);
                user.NormalizedEmail = _userManager.NormalizeEmail(user.Email);
            }
        }

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

        // The storage quota is a whole number of MB or null (which removes it): anything else is refused at the field
        if (meta.TryGetValue(MagicStrings.MaxStorageKey, out var quota) && !IsValidQuota(quota))
            throw new InvalidFieldException(MagicStrings.MaxStorageKey, MaxStorageInvalidMessage);

        // The roles of the metadata are applied: they cannot disable the user (D-043)
        await _provisioning.RefuseDeactivatedAsync(user, UserProvisioning.RequestedMetadataRoles(meta));

        // The given keys are merged into the metadata, which keeps its other keys (the storage quota is one key among
        // them, FR-ADM-16); a null value removes its key
        var merged = new Dictionary<string, object>(user.Metadata);
        foreach (var (key, value) in meta)
        {
            if (value is null or JsonElement { ValueKind: JsonValueKind.Null })
                merged.Remove(key);
            else
                merged[key] = value;
        }

        user.Metadata = merged;

        // The roles are applied only when the request names them: the ones stored with the metadata may be older than
        // the roles the user has now (they are edited through the user), and a quota update must not bring them back
        if (UserProvisioning.RequestedMetadataRoles(meta) != null)
            await _provisioning.SyncRolesAsync(user);

        await _applicationDbContext.SaveChangesAsync();
    }

    /// <summary>Why a storage quota that is not a whole number of MB, zero or more, is refused.</summary>
    public const string MaxStorageInvalidMessage = "The storage quota must be a whole number of MB, zero or more";

    private static bool IsValidQuota(object? value) => value switch
    {
        null => true,
        JsonElement { ValueKind: JsonValueKind.Null } => true,
        JsonElement { ValueKind: JsonValueKind.Number } n => n.TryGetInt64(out var mb) && mb >= 0,
        long mb => mb >= 0,
        int mb => mb >= 0,
        _ => false
    };
}
