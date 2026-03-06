using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Registry.Common;
using Registry.Web.Data.Models;
using Registry.Web.Models;
using Registry.Web.Services.Managers;

namespace Registry.Web.Models.DTO;

public class OrganizationPermissionsDto
{
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanDelete { get; set; }
    public bool CanManageMembers { get; set; }

    public static OrganizationPermissionsDto FromPermissions(OrganizationPermissions permissions)
    {
        return new OrganizationPermissionsDto
        {
            CanRead = permissions.HasAccess(AccessType.Read),
            CanWrite = permissions.HasAccess(AccessType.Write),
            CanDelete = permissions.HasAccess(AccessType.Delete),
            CanManageMembers = permissions.CanManageMembers()
        };
    }

    public static readonly OrganizationPermissionsDto ReadOnly = new()
    {
        CanRead = true,
        CanWrite = false,
        CanDelete = false,
        CanManageMembers = false
    };
}

public class OrganizationDto
{
    [Required]
    public string Slug { get; set; }

    [Required]
    public string Name { get; set; }
    public string Description { get; set; }
    public DateTime CreationDate { get; set; }
    public string Owner { get; set; }

    public bool IsPublic { get; set; }

    /// <summary>
    /// The permissions of the current user in this organization.
    /// </summary>
    public OrganizationPermissionsDto Permissions { get; set; }
}