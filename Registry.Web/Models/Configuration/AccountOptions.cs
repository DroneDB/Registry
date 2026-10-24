#nullable enable
namespace Registry.Web.Models.Configuration;

/// <summary>Account and organization features (keys of the <c>AppSettings</c> section).</summary>
public sealed class AccountOptions
{
    /// <summary>Enables the user storage limiter.</summary>
    public bool EnableStorageLimiter { get; set; }

    /// <summary>
    /// Enables organization-level member management. When disabled, only system admins can manage
    /// organization members.
    /// </summary>
    public bool EnableOrganizationMemberManagement { get; set; }

    /// <summary>
    /// Creates a default personal organization for each new user. When false, organizations must be
    /// assigned manually.
    /// </summary>
    public bool EnableDefaultUserOrganization { get; set; } = true;
}
