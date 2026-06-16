using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Registry.Web.Identity.Models;
using Registry.Web.Models;
using Registry.Web.Services.Ports;
using Registry.Web.Utilities;

namespace Registry.Web.HealthChecks;

public class UserManagerHealthCheck : IHealthCheck
{
    private readonly UserManager<User> _userManager;
    private readonly ILoginManager _loginManager;

    public UserManagerHealthCheck(UserManager<User> userManager, ILoginManager loginManager)
    {
        _userManager = userManager;
        _loginManager = loginManager;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        // BEHAVIOR CHANGE (deliberate): skip local user-manager test when an external
        // identity provider is active. The test would only verify the local store, not the
        // actual authentication path. A dedicated provider health check is registered instead.
        if (!_loginManager.Capabilities.SupportsLocalUserManagement)
            return HealthCheckResult.Healthy(
                "Skipped: user management is handled by an external identity provider");

        var testUserName = "test-" + Guid.NewGuid();
        var testPassword = Guid.NewGuid().ToString() + Guid.NewGuid().ToString().ToUpperInvariant() + "_&%$";

        var user = new User
        {
            UserName = testUserName,
            Email = testUserName.Replace("-", string.Empty) + "@test.it"
        };

        var data = new Dictionary<string, object>
        {
            {"TestUserName", testUserName},
            {"TestPassword", testPassword},
            {"TestEmail", user.Email}
        };

        try
        {
            var res = await _userManager.CreateAsync(user, testPassword);

            if (res == null || !res.Succeeded)
                return HealthCheckResult.Unhealthy("Cannot create user: " + res?.Errors.ToErrorString(), null,
                    data);

            var usr = await _userManager.FindByNameAsync(testUserName);

            if (usr == null)
                return HealthCheckResult.Unhealthy("Cannot get created user", null, data);

            res = await _userManager.DeleteAsync(user);

            if (res == null || !res.Succeeded)
                return HealthCheckResult.Unhealthy("Cannot delete user: " + res?.Errors.ToErrorString(), null,
                    data);

            return HealthCheckResult.Healthy("User manager is working properly", data);

        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Exception while testing identity db: " + ex.Message, ex, data);
        }
    }
}