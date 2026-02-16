#nullable enable
namespace Registry.Web.Services.Ports;

/// <summary>
/// Validates passwords against the configured password policy.
/// </summary>
public interface IPasswordPolicyValidator
{
    /// <summary>
    /// Validates a password against the configured policy.
    /// If no policy is configured, the password is always considered valid.
    /// </summary>
    /// <param name="password">
    /// The password to validate. May be <c>null</c>; implementations should treat <c>null</c> as invalid
    /// when a password policy is configured, and valid only when no policy is configured.
    /// </param>
    /// <returns>A result indicating whether the password is valid, with error messages if not.</returns>
    PasswordValidationResult Validate(string? password);
}

/// <summary>
/// Result of a password validation check.
/// </summary>
public class PasswordValidationResult
{
    /// <summary>
    /// Whether the password meets all policy requirements.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// List of validation error messages.
    /// </summary>
    public string[] Errors { get; set; } = [];

    public static PasswordValidationResult Success() => new() { IsValid = true };

    public static PasswordValidationResult Failure(params string[] errors) => new() { IsValid = false, Errors = errors };
}
