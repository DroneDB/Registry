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
using Registry.Web.Services.Managers;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Users;

/// <summary>Sign-in: the credentials or the external token are checked by the login provider, the user is provisioned on a first login, a deactivated user is refused, a JWT is issued.</summary>
public interface IUserSessions
{
    /// <summary>The session of <paramref name="userName"/>, or null when the provider refuses them or they are deactivated.</summary>
    Task<AuthenticateResponse> Authenticate(string userName, string password);

    /// <summary>The session of the holder of an external provider's <paramref name="token"/> (external auth only), or null.</summary>
    Task<AuthenticateResponse> Authenticate(string token);

    /// <summary>A new token for the current user.</summary>
    Task<AuthenticateResponse> Refresh();
}

/// <summary>Default <see cref="IUserSessions"/> (out of <c>UsersManager</c>).</summary>
public sealed class UserSessions : IUserSessions
{
    private readonly ILoginManager _loginManager;
    private readonly UserManager<User> _userManager;
    private readonly UserProvisioning _provisioning;
    private readonly JwtTokenIssuer _tokens;
    private readonly IAuthManager _authManager;
    private readonly IdentityProviderOptions _identity;
    private readonly ILogger<UserSessions> _logger;

    public UserSessions(ILoginManager loginManager, UserManager<User> userManager, UserProvisioning provisioning, JwtTokenIssuer tokens, IAuthManager authManager, IOptions<IdentityProviderOptions> identity, ILogger<UserSessions> logger)
    {
        _loginManager = loginManager;
        _userManager = userManager;
        _provisioning = provisioning;
        _tokens = tokens;
        _authManager = authManager;
        _identity = identity.Value;
        _logger = logger;
    }

    /// <summary>Shorthand accessor for the active provider's capability descriptor.</summary>
    private AuthProviderCapabilities Caps => _loginManager.Capabilities;

    public async Task<AuthenticateResponse> Authenticate(string userName, string password)
    {
        var res = await _loginManager.CheckAccess(userName, password);

        if (!res.Success)
            return null;

        // Create user if not exists because login manager has greenlit us.
        // For external providers (LDAP, Remote) store a random internal password that is never
        // used for authentication - the provider handles all credential verification.
        var user = await _userManager.FindByNameAsync(userName);
        if (user == null)
        {
            var localPassword = Caps.SupportsPasswordChange ? password : _provisioning.ExternalUserPassword();
            user = await _provisioning.CreateAsync(new User { UserName = userName }, localPassword);
        }

        // Deny access to deactivated accounts. External providers (LDAP/Remote) authorize the bind
        // even after a user is deactivated locally, so this guard - placed before SyncRoles, which
        // would otherwise strip the Deactivated role - is what actually enforces the deactivation.
        if (await _userManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName))
        {
            _logger.LogWarning("Authentication denied: user {UserName} is deactivated", userName);
            return null;
        }

        await _provisioning.SyncFromProviderAsync(user, res.Metadata);

        // authentication successful so generate jwt token
        var tokenDescriptor = await _tokens.IssueAsync(user);

        return new AuthenticateResponse(user.UserName, tokenDescriptor.Token, tokenDescriptor.ExpiresOn);
    }

    public async Task<AuthenticateResponse> Authenticate(string token)
    {
        // Internal auth does not support token auth
        if (_identity.ExternalAuthUrl == null)
            return null;

        var res = await _loginManager.CheckAccess(token);

        if (!res.Success)
            return null;

        // Create user if not exists because login manager has greenlighed us
        var user = await _userManager.FindByNameAsync(res.UserName) ??
                   await _provisioning.CreateAsync(new User { UserName = res.UserName }, _provisioning.ExternalUserPassword());

        // Deny access to deactivated accounts (see Authenticate(userName, password) for rationale):
        // the check runs before SyncRoles so the Deactivated role cannot be stripped by a re-login.
        if (await _userManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName))
        {
            _logger.LogWarning("Authentication denied: user {UserName} is deactivated", res.UserName);
            return null;
        }

        await _provisioning.SyncFromProviderAsync(user, res.Metadata);

        // Authentication successful so generate jwt token
        var tokenDescriptor = await _tokens.IssueAsync(user);

        return new AuthenticateResponse(user.UserName, tokenDescriptor.Token, tokenDescriptor.ExpiresOn);
    }

    public async Task<AuthenticateResponse> Refresh()
    {
        var user = await _authManager.GetCurrentUser();

        if (!await _authManager.CanRefreshToken(user))
            throw new UnauthorizedException("User cannot refresh token");

        var tokenDescriptor = await _tokens.IssueAsync(user);

        return new AuthenticateResponse(user.UserName, tokenDescriptor.Token, tokenDescriptor.ExpiresOn);
    }
}
