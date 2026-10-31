using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Registry.Common;

namespace Registry.Web.Data.Models;

/// <summary>
/// EF entity representing an organization (user group with datasets and members).
/// </summary>
public class Organization : IRequestAccess
{
    /// <summary>Unique slug identifier (max 128 chars).</summary>
    [Key]
    [MaxLength(128)]
    public string Slug { get; set; } = null!;

    /// <summary>Human-readable organization name.</summary>
    [Required]
    public string Name { get; set; } = null!;

    /// <summary>Optional organization description.</summary>
    public string? Description { get; set; }

    /// <summary>Date and time when the organization was created.</summary>
    [Required]
    public DateTime CreationDate { get; set; }

    /// <summary>User ID of the organization owner.</summary>
    public string? OwnerId { get; set; }

    /// <summary>Whether the organization is publicly visible.</summary>
    [Required]
    public bool IsPublic { get; set; }

    /// <summary>Collections of datasets belonging to this organization.</summary>
    public virtual ICollection<Dataset> Datasets { get; set; } = null!;

    /// <summary>Collection of organization member relationships.</summary>
    public virtual ICollection<OrganizationUser> Users { get; set; } = null!;
}

/// <summary>
/// Join entity for organization membership with permissions and audit fields.
/// </summary>
public class OrganizationUser
{
    /// <summary>Organization this membership belongs to.</summary>
    [Required]
    public Organization Organization { get; set; } = null!;

    /// <summary>User ID of the organization member.</summary>
    [Required]
    public string UserId { get; set; } = null!;

    /// <summary>Organization slug (denormalized for query convenience).</summary>
    public string OrganizationSlug { get; set; } = null!;

    /// <summary>
    /// Permission level for this member (0=ReadOnly, 1=ReadWrite, 2=ReadWriteDelete, 3=Admin)
    /// Default is ReadWrite (1) to maintain backward compatibility
    /// </summary>
    public OrganizationPermissions Permissions { get; set; } = OrganizationPermissions.ReadWrite;

    /// <summary>
    /// When the membership was granted
    /// </summary>
    public DateTime? GrantedAt { get; set; }

    /// <summary>
    /// User ID of who granted the membership
    /// </summary>
    public string? GrantedBy { get; set; }
}