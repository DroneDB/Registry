using System;
using Registry.Common;
using Registry.Web.Models;

namespace Registry.Web.Models.DTO;

/// <summary>
/// Data transfer object for organization members
/// </summary>
public class OrganizationMemberDto
{
    /// <summary>
    /// User's unique identifier
    /// </summary>
    public string UserId { get; set; }

    /// <summary>
    /// User's display name
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// User's email address
    /// </summary>
    public string Email { get; set; }

    /// <summary>
    /// Permission level (0=ReadOnly, 1=ReadWrite, 2=ReadWriteDelete, 3=Admin)
    /// </summary>
    public OrganizationPermissions Permissions { get; set; }

    /// <summary>
    /// When the membership was granted
    /// </summary>
    public DateTime? GrantedAt { get; set; }

    /// <summary>
    /// Username of who granted the membership
    /// </summary>
    public string GrantedBy { get; set; }
}

/// <summary>
/// Request DTO for adding a member to an organization
/// </summary>
public class AddOrganizationMemberDto
{
    /// <summary>
    /// User ID to add as member
    /// </summary>
    public string UserId { get; set; }

    /// <summary>
    /// Permission level. Default is ReadWrite
    /// </summary>
    public OrganizationPermissions Permissions { get; set; } = OrganizationPermissions.ReadWrite;
}

/// <summary>
/// Request DTO for updating a member's permissions
/// </summary>
public class UpdateMemberPermissionsDto
{
    /// <summary>
    /// New permission level
    /// </summary>
    public OrganizationPermissions Permissions { get; set; }
}
