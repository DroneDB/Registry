#nullable enable
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Registry.Web.Models.Configuration;
using Registry.Web.Services.Ports;

namespace Registry.Web.Services.Adapters;

/// <summary>
/// Validates passwords against the configured password policy.
/// When no policy is configured (null), all passwords are accepted.
/// </summary>
public class PasswordPolicyValidator : IPasswordPolicyValidator
{
    private readonly PasswordPolicy? _policy;

    public PasswordPolicyValidator(IOptions<AppSettings> appSettings)
    {
        _policy = appSettings.Value.PasswordPolicy;
    }

    public PasswordValidationResult Validate(string? password)
    {
        // No policy configured — accept any password
        if (_policy == null)
            return PasswordValidationResult.Success();

        var errors = new List<string>();

        if (string.IsNullOrEmpty(password) || password.Length < _policy.MinLength)
        {
            errors.Add($"Password must be at least {_policy.MinLength} characters long");

            // Short-circuit: if password is null or empty, no point checking further rules
            if (string.IsNullOrEmpty(password))
                return PasswordValidationResult.Failure(errors.ToArray());
        }

        if (_policy.RequireDigit && !password.Any(char.IsDigit))
            errors.Add("Password must contain at least one digit");

        if (_policy.RequireUppercase && !password.Any(char.IsUpper))
            errors.Add("Password must contain at least one uppercase letter");

        if (_policy.RequireLowercase && !password.Any(char.IsLower))
            errors.Add("Password must contain at least one lowercase letter");

        if (_policy.RequireNonAlphanumeric && password.All(char.IsLetterOrDigit))
            errors.Add("Password must contain at least one special character");

        return errors.Count > 0
            ? PasswordValidationResult.Failure(errors.ToArray())
            : PasswordValidationResult.Success();
    }
}
