#nullable enable
namespace Registry.Web.Models.DTO;

/// <summary>
/// Represents the status of all platform feature flags.
/// </summary>
public class FeaturesDto
{
    /// <summary>
    /// Whether organization-level member management is enabled.
    /// </summary>
    public bool OrganizationMemberManagement { get; set; }

    /// <summary>
    /// Whether local user management is enabled (false when external auth is configured).
    /// </summary>
    public bool UserManagement { get; set; }

    /// <summary>
    /// Whether the per-user storage limiter is enabled.
    /// </summary>
    public bool StorageLimiter { get; set; }

    /// <summary>
    /// Password complexity policy. Null when no policy is enforced.
    /// </summary>
    public PasswordPolicyDto? PasswordPolicy { get; set; }
}
