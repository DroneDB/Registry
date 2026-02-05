using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Text;
using Registry.Common;

namespace Registry.Web.Data.Models;

public class Organization : IRequestAccess
{
    [Key]
    [MaxLength(128)]
    public string Slug { get; set; }
    [Required]
    public string Name { get; set; }
    public string Description { get; set; }
    [Required]
    public DateTime CreationDate { get; set; }
    public string OwnerId { get; set; }

    [Required]
    public bool IsPublic { get; set; }

    public virtual ICollection<Dataset> Datasets { get; set; }
    public virtual ICollection<OrganizationUser> Users { get; set; }
}

public class OrganizationUser
{
    [Required]
    public Organization Organization { get; set; }

    [Required]
    public string UserId { get; set; }

    public string OrganizationSlug { get; set; }

    /// <summary>
    /// Permission level for this member (0=ReadOnly, 1=ReadWrite, 2=ReadWriteDelete, 3=Admin)
    /// Default is ReadWrite (1) to maintain backward compatibility
    /// </summary>
    public OrganizationPermission Permissions { get; set; } = OrganizationPermission.ReadWrite;

    /// <summary>
    /// When the membership was granted
    /// </summary>
    public DateTime? GrantedAt { get; set; }

    /// <summary>
    /// User ID of who granted the membership
    /// </summary>
    public string GrantedBy { get; set; }
}