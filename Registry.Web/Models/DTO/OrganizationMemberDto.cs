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
    /// User's display name (unique identifier)
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
    /// Username to add as member
    /// </summary>
    public string UserName { get; set; }

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

/// <summary>
/// DTO representing a user's membership in an organization, including org info and membership details
/// </summary>
public class UserOrganizationMembershipDto
{
    /// <summary>
    /// Organization slug (unique identifier)
    /// </summary>
    public string Slug { get; set; }

    /// <summary>
    /// Organization display name
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Organization description
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Whether the organization is public
    /// </summary>
    public bool IsPublic { get; set; }

    /// <summary>
    /// Whether the user is the owner of this organization (cannot be removed)
    /// </summary>
    public bool IsOwner { get; set; }

    /// <summary>
    /// Permission level (0=ReadOnly, 1=ReadWrite, 2=ReadWriteDelete, 3=Admin). Null if owner.
    /// </summary>
    public OrganizationPermissions? Permissions { get; set; }

    /// <summary>
    /// When the membership was granted (null for owners)
    /// </summary>
    public DateTime? GrantedAt { get; set; }

    /// <summary>
    /// Username of who granted the membership (null for owners)
    /// </summary>
    public string GrantedBy { get; set; }
}

/// <summary>
/// Request DTO for adding a user to an organization (from user management perspective)
/// </summary>
public class AddUserToOrganizationDto
{
    /// <summary>
    /// Organization slug to add the user to
    /// </summary>
    public string OrgSlug { get; set; }

    /// <summary>
    /// Permission level. Default is ReadWrite
    /// </summary>
    public OrganizationPermissions Permissions { get; set; } = OrganizationPermissions.ReadWrite;
}
