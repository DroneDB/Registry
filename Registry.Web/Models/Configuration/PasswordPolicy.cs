namespace Registry.Web.Models.Configuration;

/// <summary>
/// Defines password complexity requirements.
/// When this object is null in AppSettings, no password policy is enforced.
/// </summary>
public class PasswordPolicy
{
    /// <summary>
    /// Minimum password length. Default is 8.
    /// </summary>
    public int MinLength { get; set; } = 8;

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
