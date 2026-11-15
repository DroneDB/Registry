using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Registry.Web.Exceptions;
using Registry.Web.Identity;
using Registry.Web.Identity.Models;
using Registry.Web.Models.DTO;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Managers;

/// <summary>
/// Local (Identity-based) login implementation for <see cref="ILoginManager"/>.
/// </summary>
public class LocalLoginManager : ILoginManager
{
    private readonly UserManager<User> _userManager;
    private readonly SignInManager<User> _signInManager;

    public LocalLoginManager(UserManager<User> userManager, SignInManager<User> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    public AuthProviderCapabilities Capabilities => AuthProviderCapabilities.Local;

    public Task<LoginResultDto> CheckAccess(string token)
    {
        // No token login for local auth
        return Task.FromResult(new LoginResultDto
        {
            Success = false
        });
    }

    /// <summary>
    /// Checks the password under Identity's lockout: a wrong password is a failed access, the account is locked after
    /// the configured number of failures (5, for 5 minutes) and a locked account is refused with 429 and Retry-After,
    /// even with its right password, until the lockout ends; a right password clears the failures. The sign-in route
    /// also limits the attempts of each client address (RateLimitPolicies.Login), which a lockout alone does not.
    /// </summary>
    /// <exception cref="TooManyRequestsException">The account is locked out.</exception>
    public async Task<LoginResultDto> CheckAccess(string userName, string password)
    {
        var user = await _userManager.FindByNameAsync(userName);

        if (user == null || await _userManager.IsInRoleAsync(user, ApplicationDbContext.DeactivatedRoleName))
            return new LoginResultDto
            {
                Success = false,
                UserName = userName
            };

        var res = await _signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (res.IsLockedOut)
            throw TooManyRequestsException.AccountLockedOut(await _userManager.GetLockoutEndDateAsync(user));

        return new LoginResultDto
        {
            Success = res.Succeeded,
            UserName = userName,
            Metadata = user.Metadata
        };
    }

}