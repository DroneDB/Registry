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

    public async Task<ChangePasswordResult> ChangePassword(string userName, string? currentPassword, string newPassword)
    {
        var user = await _userManager.FindByNameAsync(userName);

        if (user == null)
            throw new BadRequestException("User does not exist");

        return await ChangePasswordInternal(user, currentPassword, newPassword);
    }

    public async Task<ChangePasswordResult> ChangePassword(string? currentPassword, string newPassword)
    {
        var currentUser = await _authManager.GetCurrentUser();

        if (currentUser == null)
            throw new BadRequestException("User is not authenticated");

        return await ChangePasswordInternal(currentUser, currentPassword, newPassword);
    }

    private async Task<ChangePasswordResult> ChangePasswordInternal(User user, string? currentPassword,
        string newPassword)
    {
        if (!Caps.SupportsPasswordChange)
            throw new ForbiddenException(
                "Password changes are not supported for accounts managed by an external identity provider. " +
                "Please use your organisation's directory service to change your password.", noRetry: true);

        IdentityResult res;

        if (currentPassword == null)
        {
            // Admin override: reset password without requiring the current one
            if (!await _authManager.IsUserAdmin())
                throw new ForbiddenException("Current password is required for non-admin users");

            if (newPassword == null)
                throw new BadRequestException("New password cannot be null");

            var adminUser = await _authManager.GetCurrentUser();
            _logger.LogInformation(
                "Admin user {AdminUserName} is resetting password for {TargetUserName}",
                adminUser?.UserName, user.UserName);

            var resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
            res = await _userManager.ResetPasswordAsync(user, resetToken, newPassword);
        }
        else
        {
            // Self-service password change: validate against policy
            _logger.LogInformation("User {UserName} is changing their own password", user.UserName);

            var policyResult = _passwordPolicyValidator.Validate(newPassword);
            if (!policyResult.IsValid)
                throw new BadRequestException("Password does not meet the requirements: " + string.Join("; ", policyResult.Errors));

            res = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        }

        if (!res.Succeeded)
        {
            var errors = string.Join(";", res.Errors.Select(item => $"{item.Code} - {item.Description}"));
            _logger.LogWarning("Errors in changing user password: {Errors}", errors);

            throw new BadRequestException("Cannot change user password: " + errors);
        }

        _logger.LogInformation("User {UserName} changed password", user.UserName);

        return new ChangePasswordResult
        {
            UserName = user.RequiredUserName(),
            Password = newPassword
        };

    }
}
