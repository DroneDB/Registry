using Registry.Web.Models.Configuration;

namespace Registry.Web.Models.DTO;

/// <summary>
/// DTO for exposing password policy requirements to the frontend.
/// </summary>
public class PasswordPolicyDto
{
    /// <summary>
    /// Minimum password length.
    /// </summary>
    public int MinLength { get; set; }

    /// <summary>
    /// Whether the password must contain at least one digit.
    /// </summary>
    public bool RequireDigit { get; set; }

    /// <summary>
    /// Whether the password must contain at least one uppercase letter.
    /// </summary>
    public bool RequireUppercase { get; set; }

    /// <summary>
    /// Whether the password must contain at least one lowercase letter.
    /// </summary>
    public bool RequireLowercase { get; set; }

    /// <summary>
    /// Whether the password must contain at least one non-alphanumeric character.
    /// </summary>
    public bool RequireNonAlphanumeric { get; set; }
}
