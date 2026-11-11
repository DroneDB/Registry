namespace Registry.Web.Models.Configuration;

/// <summary>Account and organization features (keys of the <c>AppSettings</c> section).</summary>
public sealed class AccountOptions
{
    /// <summary>Enables the user storage limiter.</summary>
    public bool EnableStorageLimiter { get; set; }

    /// <summary>
    /// Enables organization-level member management: the owner, members with the Admin permission
    /// and system admins add, change and remove members. When disabled, the member endpoints answer
    /// 403 to everyone, system admins included.
    /// </summary>
    public bool EnableOrganizationMemberManagement { get; set; }

    /// <summary>
    /// Creates a default personal organization for each new user. When false, organizations must be
    /// assigned manually.
    /// </summary>
    public bool EnableDefaultUserOrganization { get; set; } = true;
}
