using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Registry.Web.Exceptions;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Users;

/// <summary>Password changes: by the user (current password, policy), or a reset by an administrator.</summary>
public sealed class UserPasswords
{
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly IAuthManager _authManager;
    private readonly IPasswordPolicyValidator _passwordPolicyValidator;
    private readonly ILogger<UserPasswords> _logger;

    public UserPasswords(ILoginManager loginManager, UserManager<User> userManager, IAuthManager authManager, IPasswordPolicyValidator passwordPolicyValidator, ILogger<UserPasswords> logger)
    {
        _loginManager = loginManager;
        _userManager = userManager;
        _authManager = authManager;
        _passwordPolicyValidator = passwordPolicyValidator;
        _logger = logger;
    }

    /// <summary>Shorthand accessor for the active provider's capability descriptor.</summary>
    private AuthProviderCapabilities Caps => _loginManager.Capabilities;

    /// <summary>
    /// Changes the password of <paramref name="userName"/>: an administrator changes any (and resets it without the
    /// current password), any other user only their own, so the current password of another user cannot be tried here.
    /// </summary>
    /// <exception cref="ForbiddenException">A user other than an administrator names another user.</exception>
    public async Task<ChangePasswordResult> ChangePassword(string userName, string? currentPassword, string newPassword)
    {
        // Checked before the lookup, so that the answer tells nothing of the users that exist
        var changer = await ChangerOf(userName);
        Authorize(changer, currentPassword);

        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("User does not exist");

        return await ChangePasswordInternal(user, currentPassword, newPassword, changer);
    }

    public async Task<ChangePasswordResult> ChangePassword(string? currentPassword, string newPassword)
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null)
            throw new BadRequestException("User is not authenticated");

        var changer = await ChangerOf(currentUser.RequiredUserName());
        Authorize(changer, currentPassword);
        return await ChangePasswordInternal(currentUser, currentPassword, newPassword, changer);
    }

    /// <summary>Who changes a password: the caller, whether it is the user of the password, whether an administrator.</summary>
    private sealed record Changer(User? Caller, bool Self, bool Admin);

    private async Task<Changer> ChangerOf(string userName)
    {
        var caller = await _authManager.GetCurrentUser();
        var self = caller != null &&
                   string.Equals(_userManager.NormalizeName(caller.UserName), _userManager.NormalizeName(userName),
                       StringComparison.Ordinal);
        return new Changer(caller, self, await _authManager.IsUserAdmin());
    }

    /// <summary>
    /// The one rule of who changes a password: the user, with the current password, or an administrator, any password
    /// and without the current one too (a reset).
    /// </summary>
    /// <exception cref="ForbiddenException">Neither, or a user who is not an administrator without the current password.</exception>
    private static void Authorize(Changer changer, string? currentPassword)
    {
        if (!changer.Self && !changer.Admin)
            throw new ForbiddenException("You can change only your own password");

        if (currentPassword == null && !changer.Admin)
            throw new ForbiddenException("Current password is required for non-admin users");
    }

    /// <summary>Member of the request that carries the current password (D-047).</summary>
    public const string CurrentPasswordField = "currentPassword";

    /// <summary>Member of the request that carries the new password (D-047).</summary>
    public const string NewPasswordField = "newPassword";

    /// <summary>The message of a wrong current password (Identity's own text names no field).</summary>
    public const string CurrentPasswordIncorrect = "The current password is incorrect.";

    /// <summary>
    /// The one place that maps Identity's password refusals to the field they concern (D-047, FR-ADM-08,
    /// FR-ACC-21): <c>PasswordMismatch</c> to the current password, the other password rules (length,
    /// digit, case, symbol, ...) to the new one, as the Identity descriptions joined by spaces without their
    /// codes. A refusal that concerns no field (a locked user, a concurrency failure) is left out: with no
    /// field at all the answer carries no <c>errors</c> member and the Hub shows its message form-level.
    /// </summary>
    private static Dictionary<string, string[]>? FieldErrorsOf(IEnumerable<IdentityError> errors)
    {
        var list = errors.ToList();
        var fields = new Dictionary<string, string[]>();

        if (list.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
            fields[CurrentPasswordField] = [CurrentPasswordIncorrect];

        var policy = list
            .Where(e => e.Code.StartsWith("Password", StringComparison.Ordinal) &&
                        e.Code != nameof(IdentityErrorDescriber.PasswordMismatch))
            .Select(e => e.Description)
            .ToList();
        if (policy.Count > 0)
            fields[NewPasswordField] = [string.Join(" ", policy)];

        return fields.Count > 0 ? fields : null;
    }

    // Authorized for the changer (Authorize). The user's own change: the current password given is a sign-in attempt
    private async Task<ChangePasswordResult> ChangePasswordInternal(User user, string? currentPassword,
        string newPassword, Changer changer)
    {
        if (!Caps.SupportsPasswordChange)
            throw new ForbiddenException(
                "Password changes are not supported for accounts managed by an external identity provider. " +
                "Please use your organisation's directory service to change your password.", noRetry: true);

        IdentityResult res;

        if (currentPassword == null)
        {
            // Admin override (Authorize): reset password without requiring the current one
            if (newPassword == null)
                throw new BadRequestException("New password cannot be null");

            _logger.LogInformation(
                "Admin user {AdminUserName} is resetting password for {TargetUserName}",
                changer.Caller?.UserName, user.UserName);

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            res = await _userManager.ResetPasswordAsync(user, resetToken, newPassword);
        }
        else
        {
            // Self-service password change: validate against policy
            _logger.LogInformation("User {UserName} is changing their own password", user.UserName);

            var policyResult = _passwordPolicyValidator.Validate(newPassword);
            if (!policyResult.IsValid)
                throw new BadRequestException("Password does not meet the requirements: " + string.Join("; ", policyResult.Errors))
                {
                    FieldErrors = new Dictionary<string, string[]>
                    {
                        [NewPasswordField] = [string.Join(" ", policyResult.Errors)]
                    }
                };

            res = changer.Self
                ? await ChangeOwnPasswordAsync(user, currentPassword, newPassword)
                : await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        }

        if (!res.Succeeded)
        {
            var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
            _logger.LogWarning("Errors in changing user password: {Errors}", errors);

            throw new BadRequestException("Cannot change user password: " + errors)
            {
                FieldErrors = FieldErrorsOf(res.Errors)
            };
        }

        _logger.LogInformation("User {UserName} changed password", user.UserName);

        return new ChangePasswordResult
        {
            UserName = user.RequiredUserName(),
            Password = newPassword
        };

    }

    /// <summary>
    /// The change of a user's own password, under Identity's lockout as a sign-in is: refused while the user is locked
    /// out, a wrong current password is a failed access (<see cref="UserManager{TUser}.AccessFailedAsync"/>, which
    /// locks the user out after the configured number of failures) and a right one clears the failures, so the current
    /// password cannot be guessed here faster than at the sign-in.
    /// </summary>
    /// <exception cref="TooManyRequestsException">The user is locked out.</exception>
    private async Task<IdentityResult> ChangeOwnPasswordAsync(User user, string currentPassword, string newPassword)
    {
        if (await _userManager.IsLockedOutAsync(user))
            throw TooManyRequestsException.AccountLockedOut(await _userManager.GetLockoutEndDateAsync(user));

        var res = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (res.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch)))
        {
            await _userManager.AccessFailedAsync(user);
            if (await _userManager.IsLockedOutAsync(user))
                throw TooManyRequestsException.AccountLockedOut(await _userManager.GetLockoutEndDateAsync(user));
        }
        else if (await _userManager.GetAccessFailedCountAsync(user) > 0)
            await _userManager.ResetAccessFailedCountAsync(user);

        return res;
    }
}
